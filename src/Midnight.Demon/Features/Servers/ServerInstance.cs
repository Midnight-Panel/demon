using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Features.Docker;
using Midnight.Demon.Features.Servers.ConfigFiles;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Demon.Features.Servers.Output;
using Midnight.Shared.Enums;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Http.Servers;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Features.Servers;

/// <summary>
/// Runtime of one server: its configuration, the container state, the console and the live stats.
/// Power and install operations hold <see cref="OperationLock"/>, kill never waits for it.
/// </summary>
public sealed class ServerInstance
{
    public const int HistoryLines = 150;
    private const long MiB = 1024 * 1024;

    private static readonly TimeSpan StatsBroadcastInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(15);

    private readonly DockerService _docker;
    private readonly ServerFileSystem _files;
    private readonly ServerRuntimeConfiguration _options;
    private readonly ILogger _logger;
    private readonly CancellationToken _shutdown;

    // _stateLock guards the state and the running container, _eventsLock the console and the subscribers.
    // Lock order: _stateLock → _eventsLock
    private readonly Lock _stateLock = new();
    private readonly Lock _eventsLock = new();
    private readonly ConsoleBuffer _console = new(HistoryLines);
    private readonly List<ServerEventSubscription> _subscribers = [];

    private DemonServerConfiguration _configuration;
    private ServerState _state = ServerState.Offline;
    private RunningContainer? _container;
    private ContainerUsage _usage = ContainerUsage.Empty;
    private CancellationTokenSource? _installCancellation;
    private long _diskBytes;
    private volatile bool _installing;
    private volatile bool _restoring;
    private volatile bool _deleted;

    internal ServerInstance(DemonServerConfiguration configuration, DockerService docker, ServerFileSystem files,
        ServerRuntimeConfiguration options, ILogger logger, CancellationToken shutdown)
    {
        Uuid = configuration.Uuid;
        _configuration = configuration;
        _docker = docker;
        _files = files;
        _options = options;
        _logger = logger;
        _shutdown = shutdown;
    }

    public Guid Uuid { get; }
    public string ContainerName => ContainerParameters.ServerContainerName(Uuid);
    public DemonServerConfiguration Configuration => Volatile.Read(ref _configuration);
    public bool Installing => _installing;
    public bool Restoring => _restoring;
    public bool Deleted => _deleted;
    public long DiskBytes => Interlocked.Read(ref _diskBytes);

    public ServerState State
    {
        get
        {
            lock (_stateLock)
                return _state;
        }
    }

    /// <summary>True while a container is attached (Starting, Online or Stopping)</summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
                return _container != null;
        }
    }

    /// <summary>Serializes power, install and delete operations of this server</summary>
    internal SemaphoreSlim OperationLock { get; } = new(1, 1);

    public DemonServerStateResponse GetState() => new()
    {
        Uuid = Uuid,
        State = State,
        Installing = Installing,
        Stats = GetStats()
    };

    public ServerStatsDto GetStats()
    {
        RunningContainer? running;
        ContainerUsage usage;

        lock (_stateLock)
        {
            running = _container;
            usage = _usage;
        }

        return new ServerStatsDto
        {
            CpuPercent = running == null ? 0 : usage.CpuPercent,
            MemoryBytes = running == null ? 0 : usage.MemoryBytes,
            MemoryLimitBytes = Configuration.Limits.Memory * MiB,
            DiskBytes = DiskBytes,
            NetworkRxBytes = running == null ? 0 : usage.NetworkRxBytes,
            NetworkTxBytes = running == null ? 0 : usage.NetworkTxBytes,
            UptimeSeconds = running?.StartedAt is { } started ? Math.Max(0, (long)(DateTimeOffset.UtcNow - started).TotalSeconds) : 0
        };
    }

    public List<string> GetConsoleLines(int count) => _console.GetLines(count);

    // ─── Events ──────────────────────────────────────────────────────────────

    /// <summary>Subscribes to the live events. <paramref name="history"/> are the console lines before the subscription, no line is lost or duplicated.</summary>
    public ServerEventSubscription Subscribe(out List<string> history)
    {
        lock (_eventsLock)
        {
            var subscription = new ServerEventSubscription(this);

            if (_deleted)
                subscription.Complete();
            else
                _subscribers.Add(subscription);

            history = _console.GetLines(HistoryLines);
            return subscription;
        }
    }

    internal void Unsubscribe(ServerEventSubscription subscription)
    {
        lock (_eventsLock)
            _subscribers.Remove(subscription);
    }

    /// <summary>Ends every subscription (the websockets close)</summary>
    internal void CompleteSubscriptions()
    {
        lock (_eventsLock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Complete();

            _subscribers.Clear();
        }
    }

    internal void Broadcast(string @event, object? data)
    {
        var message = new ConsoleMessage { Event = @event, Data = data };

        lock (_eventsLock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Write(message);
        }
    }

    /// <summary>Something the users of the console should see</summary>
    internal void Notice(string message)
    {
        _logger.LogInformation("Server {Uuid}: {Notice}", Uuid, message);

        // Also kept in the history, so whoever opens the console later still sees why e.g. an install failed
        lock (_eventsLock)
            _console.Add($"\u001b[33m[Midnight]\u001b[0m {message}");

        Broadcast(ConsoleEvents.Notice, message);
    }

    internal void SetState(ServerState state)
    {
        lock (_stateLock)
            SetStateLocked(state);
    }

    private void SetStateLocked(ServerState state)
    {
        if (_state == state)
            return;

        _logger.LogDebug("Server {Uuid} is now {State}", Uuid, state);
        _state = state;
        Broadcast(ConsoleEvents.Status, state);
    }

    private void OnConsoleLine(string line)
    {
        lock (_eventsLock)
        {
            _console.Add(line);

            var message = new ConsoleMessage { Event = ConsoleEvents.Console, Data = line };

            foreach (var subscriber in _subscribers)
                subscriber.Write(message);
        }

        if (StartupDetector.IsDone(line, Configuration.Process.StartupDone))
        {
            lock (_stateLock)
            {
                if (_state == ServerState.Starting && _container != null)
                    SetStateLocked(ServerState.Online);
            }
        }
    }

    // ─── Configuration ───────────────────────────────────────────────────────

    /// <summary>Replaces the configuration. Limits docker can change are applied to a running container right away, everything else on the next start.</summary>
    public async Task UpdateConfigurationAsync(DemonServerConfiguration configuration, CancellationToken cancellationToken)
    {
        var previous = Interlocked.Exchange(ref _configuration, configuration);

        if (!IsRunning)
            return;

        if (configuration.Suspended && !previous.Suspended)
        {
            Notice("The server has been suspended");
            await KillAsync(cancellationToken);
            return;
        }

        try
        {
            await _docker.Client.Containers.UpdateContainerAsync(ContainerName, ContainerParameters.ForUpdate(configuration, _options), cancellationToken);
            _logger.LogInformation("Updated the limits of the running server {Uuid}", Uuid);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not update the limits of the running server {Uuid}, they apply on the next start: {Error}", Uuid, DockerErrors.Describe(e));
        }
    }

    // ─── Disk ────────────────────────────────────────────────────────────────

    public async Task<long> MeasureDiskAsync(CancellationToken cancellationToken)
    {
        var path = _files.GetVolumePath(Uuid);
        var size = await Task.Run(() => ServerFileSystem.GetDirectorySize(path), cancellationToken);

        Interlocked.Exchange(ref _diskBytes, size);
        return size;
    }

    /// <summary>Counts bytes written or freed by the file manager until the next measurement</summary>
    internal void AddDiskBytes(long delta)
    {
        if (Interlocked.Add(ref _diskBytes, delta) < 0)
            Interlocked.Exchange(ref _diskBytes, 0);
    }

    /// <summary>Bytes the server may still write, long.MaxValue without disk limit</summary>
    public long GetRemainingDiskBytes()
    {
        var limit = Configuration.Limits.Disk * MiB;
        return limit <= 0 ? long.MaxValue : Math.Max(0, limit - DiskBytes);
    }

    /// <summary>True when the disk limit is exceeded, measured again when the last measurement says so (the user may have deleted files)</summary>
    private async Task<bool> IsDiskExceededAsync(CancellationToken cancellationToken)
    {
        var limit = Configuration.Limits.Disk * MiB;

        if (limit <= 0 || DiskBytes <= limit)
            return false;

        return await MeasureDiskAsync(cancellationToken) > limit;
    }

    // ─── Power ───────────────────────────────────────────────────────────────

    /// <summary>Why the action can't run right now, null = allowed. Stop and kill are always allowed (no-op when offline).</summary>
    public async Task<string?> CheckPowerAsync(PowerAction action, CancellationToken cancellationToken)
    {
        if (_deleted)
            return "The server is being deleted";

        if (action is PowerAction.Stop or PowerAction.Kill)
            return null;

        if (_installing)
            return "The server is being installed";

        if (_restoring)
            return "A backup is being restored";

        if (Configuration.Suspended)
            return "The server is suspended";

        if (action == PowerAction.Start && IsRunning)
            return "The server is already running";

        if (await IsDiskExceededAsync(cancellationToken))
            return "The disk limit of the server is exceeded, free up some space before starting it";

        return null;
    }

    public async Task PowerAsync(PowerAction action, CancellationToken cancellationToken)
    {
        if (action == PowerAction.Kill)
        {
            await KillAsync(cancellationToken);
            return;
        }

        await OperationLock.WaitAsync(cancellationToken);

        try
        {
            switch (action)
            {
                case PowerAction.Start:
                    await StartLockedAsync(cancellationToken);
                    break;
                case PowerAction.Stop:
                    await StopLockedAsync(cancellationToken);
                    break;
                case PowerAction.Restart:
                    await StopLockedAsync(cancellationToken);
                    await StartLockedAsync(cancellationToken);
                    break;
            }
        }
        finally
        {
            OperationLock.Release();
        }
    }

    /// <summary>(Re)creates the container from the current configuration and starts it. Problems are reported as notice, never thrown.</summary>
    private async Task StartLockedAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
            return;

        var refusal = await CheckPowerAsync(PowerAction.Start, cancellationToken);

        if (refusal != null)
        {
            Notice(refusal);
            return;
        }

        var configuration = Configuration;
        RunningContainer? running = null;

        SetState(ServerState.Starting);

        try
        {
            var volume = _files.EnsureVolume(Uuid);
            await PrepareVolumeAsync(configuration, volume, cancellationToken);

            await _docker.EnsureImageAsync(configuration.DockerImage, Notice, cancellationToken);

            await _docker.RemoveContainerIfExistsAsync(ContainerName, cancellationToken);
            await _docker.Client.Containers.CreateContainerAsync(ContainerParameters.ForServer(configuration, volume, _options), cancellationToken);

            // attach before the start, otherwise the first lines would be lost
            var stream = await AttachAsync(cancellationToken);
            running = new RunningContainer(stream, _shutdown);

            lock (_stateLock)
            {
                _container = running;
                _usage = ContainerUsage.Empty;
            }

            running.OutputTask = Task.Run(() => PumpOutputAsync(running), CancellationToken.None);

            await _docker.Client.Containers.StartContainerAsync(ContainerName, new ContainerStartParameters(), cancellationToken);
            running.StartedAt = DateTimeOffset.UtcNow;

            _ = Task.Run(() => MonitorExitAsync(running), CancellationToken.None);
            _ = Task.Run(() => MonitorStatsAsync(running), CancellationToken.None);

            _logger.LogInformation("Started server {Uuid} ({Name})", Uuid, configuration.Name);

            if (!StartupDetector.HasPatterns(configuration.Process.StartupDone))
            {
                lock (_stateLock)
                {
                    if (_state == ServerState.Starting && _container == running)
                        SetStateLocked(ServerState.Online);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // expected docker problems without stack trace, everything else with it
            if (e is DockerApiException || DockerErrors.IsUnavailable(e))
                _logger.LogError("Starting server {Uuid} failed: {Error}", Uuid, DockerErrors.Describe(e));
            else
                _logger.LogError(e, "Starting server {Uuid} failed", Uuid);

            if (running != null)
                Detach(running);

            Notice($"The server could not be started: {DockerErrors.Describe(e)}");
            SetState(ServerState.Offline);
        }
        catch (OperationCanceledException)
        {
            if (running != null)
                Detach(running);

            SetState(ServerState.Offline);
            throw;
        }
    }

    /// <summary>Config files, eula and permissions before a start. Problems are reported but don't prevent the start.</summary>
    private async Task PrepareVolumeAsync(DemonServerConfiguration configuration, string volume, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var written = ConfigFilePatcher.Apply(volume, configuration.Process.ConfigFiles, problems);

        if (configuration.Process.Features.Eula)
        {
            try
            {
                written.Add(ConfigFilePatcher.WriteEula(volume));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"eula.txt could not be written: {e.Message}");
            }
        }

        foreach (var problem in problems)
        {
            _logger.LogWarning("Server {Uuid}: {Problem}", Uuid, problem);
            Notice(problem);
        }

        try
        {
            if (_options.FixPermissionsOnStart)
                await Task.Run(() => _files.FixPermissions(volume), cancellationToken);
            else
                written.ForEach(_files.FixPermissions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not fix the file permissions of server {Uuid}: {Error}", Uuid, e.Message);
        }
    }

    private Task<MultiplexedStream> AttachAsync(CancellationToken cancellationToken) =>
        _docker.Client.Containers.AttachContainerAsync(ContainerName, tty: false, new ContainerAttachParameters
        {
            Stream = true,
            Stdin = true,
            Stdout = true,
            Stderr = true
        }, cancellationToken);

    /// <summary>Stop command or stop signal, killed after the stop timeout</summary>
    private async Task StopLockedAsync(CancellationToken cancellationToken)
    {
        RunningContainer? running;

        lock (_stateLock)
        {
            running = _container;

            if (running == null)
                return;

            running.StopRequested = true;
            SetStateLocked(ServerState.Stopping);
        }

        var process = Configuration.Process;
        var stopCommand = process.StopCommand?.Trim();

        if (!string.IsNullOrEmpty(stopCommand) && stopCommand != "^C")
        {
            try
            {
                await WriteStdinAsync(running, stopCommand, cancellationToken);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogWarning("Could not send the stop command to server {Uuid}, sending the stop signal: {Error}", Uuid, e.Message);
                await _docker.SignalAsync(ContainerName, ContainerParameters.NormalizeSignal(process.StopSignal), cancellationToken);
            }
        }
        else
        {
            // "^C" is the convention for "press ctrl+c" = SIGINT
            var signal = stopCommand == "^C" ? "SIGINT" : ContainerParameters.NormalizeSignal(process.StopSignal);
            await _docker.SignalAsync(ContainerName, signal, cancellationToken);
        }

        if (await WaitForExitAsync(running, TimeSpan.FromSeconds(_options.StopTimeoutSeconds), cancellationToken))
            return;

        Notice($"The server did not stop within {_options.StopTimeoutSeconds} seconds and was killed");
        await _docker.SignalAsync(ContainerName, "SIGKILL", cancellationToken);
        await WaitForExitAsync(running, KillTimeout, cancellationToken);
    }

    /// <summary>SIGKILL, does not wait for the operation lock so it also works during a hanging stop</summary>
    public async Task KillAsync(CancellationToken cancellationToken)
    {
        RunningContainer? running;

        lock (_stateLock)
        {
            running = _container;

            if (running != null)
            {
                running.StopRequested = true;
                SetStateLocked(ServerState.Stopping);
            }
        }

        // also kill a container we lost track of, e.g. when suspending
        await _docker.SignalAsync(ContainerName, "SIGKILL", cancellationToken);

        if (running != null)
            await WaitForExitAsync(running, KillTimeout, cancellationToken);
    }

    private static async Task<bool> WaitForExitAsync(RunningContainer running, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await running.Exited.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // detached because the demon shuts down
            return true;
        }
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Writes a line to the stdin of the server, throws 409 when it does not run</summary>
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        RunningContainer? running;

        lock (_stateLock)
            running = _container;

        if (running == null)
            throw ServerApiException.Conflict("The server is not running");

        try
        {
            await WriteStdinAsync(running, command, cancellationToken);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw ServerApiException.Conflict("The server is not running");
        }
    }

    private static async Task WriteStdinAsync(RunningContainer running, string command, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(command.TrimEnd('\r', '\n') + "\n");

        await running.StdinLock.WaitAsync(cancellationToken);

        try
        {
            await running.Stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        }
        finally
        {
            running.StdinLock.Release();
        }
    }

    // ─── Container monitoring ────────────────────────────────────────────────

    private async Task PumpOutputAsync(RunningContainer running)
    {
        try
        {
            await ContainerOutput.PumpAsync(running.Stream, OnConsoleLine, running.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the container exited or was detached
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Reading the console of server {Uuid} failed", Uuid);
        }
    }

    /// <summary>Waits until the container exits, then marks the server as offline (crash notice when nobody stopped it)</summary>
    private async Task MonitorExitAsync(RunningContainer running)
    {
        try
        {
            var exitCode = await WaitForContainerExitAsync(running.Token);

            if (exitCode == null)
                return;

            var oomKilled = false;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var inspect = await _docker.Client.Containers.InspectContainerAsync(ContainerName, timeout.Token);
                oomKilled = inspect.State?.OOMKilled == true;
            }
            catch (Exception e) when (e is DockerApiException or OperationCanceledException or HttpRequestException or IOException or TimeoutException)
            {
                // only used for the message
            }

            // let the last lines reach the console before the status changes
            try
            {
                await running.OutputTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // the stream will be disposed below
            }

            OnContainerExited(running, exitCode.Value, oomKilled);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Monitoring the container of server {Uuid} failed", Uuid);
        }
    }

    /// <summary>The exit code, or null when the demon shuts down / the container was detached</summary>
    private async Task<long?> WaitForContainerExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var result = await _docker.Client.Containers.WaitContainerAsync(ContainerName, cancellationToken);
                return result.StatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e) when (DockerErrors.IsNotFound(e))
            {
                return -1;
            }
            catch (Exception e)
            {
                _logger.LogWarning("Lost the connection to the container of server {Uuid}: {Error}", Uuid, e.Message);
            }

            // docker restarted or the socket broke: check the container until it is gone or we can wait again
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

                var inspect = await _docker.Client.Containers.InspectContainerAsync(ContainerName, cancellationToken);

                if (inspect.State is { Running: false })
                    return inspect.State.ExitCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e) when (DockerErrors.IsNotFound(e))
            {
                return -1;
            }
            catch (Exception e)
            {
                _logger.LogDebug("Docker is still not reachable for server {Uuid}: {Error}", Uuid, e.Message);
            }
        }
    }

    /// <summary>Exit code 0 means the program finished by itself (e.g. a script), that is no crash</summary>
    internal static string DescribeUnexpectedExit(long exitCode, bool oomKilled) => (exitCode, oomKilled) switch
    {
        (_, true) => $"The server crashed (exit code {exitCode}, out of memory)",
        (0, _) => "The server process finished on its own (exit code 0)",
        _ => $"The server crashed (exit code {exitCode})"
    };

    private void OnContainerExited(RunningContainer running, long exitCode, bool oomKilled)
    {
        bool crashed;

        lock (_stateLock)
        {
            if (_container != running)
                return;

            _container = null;
            _usage = ContainerUsage.Empty;
            crashed = !running.StopRequested && _state is ServerState.Starting or ServerState.Online;
        }

        running.Dispose();
        running.Exited.TrySetResult(exitCode);

        _logger.LogInformation("Server {Uuid} exited with code {ExitCode}", Uuid, exitCode);

        if (crashed)
            Notice(DescribeUnexpectedExit(exitCode, oomKilled));

        lock (_stateLock)
        {
            if (_state != ServerState.Installing)
                SetStateLocked(ServerState.Offline);
        }
    }

    private async Task MonitorStatsAsync(RunningContainer running)
    {
        var token = running.Token;
        var lastBroadcast = DateTimeOffset.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // the raw stream overload is obsolete in Docker.DotNet but the only one that can be cancelled cleanly (see ContainerStatsSample)
#pragma warning disable CS0618
                await using var stream = await _docker.Client.Containers.GetContainerStatsAsync(ContainerName, new ContainerStatsParameters { Stream = true }, token);
#pragma warning restore CS0618
                await using var registration = token.Register(stream.Dispose);
                using var reader = new StreamReader(stream);

                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var sample = ContainerStatsSample.Parse(line);

                    if (sample == null)
                        continue;

                    lock (_stateLock)
                    {
                        if (_container != running)
                            return;

                        _usage = ContainerUsage.From(sample);
                    }

                    var now = DateTimeOffset.UtcNow;

                    if (now - lastBroadcast >= StatsBroadcastInterval)
                    {
                        lastBroadcast = now;
                        Broadcast(ConsoleEvents.Stats, GetStats());
                    }
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (DockerErrors.IsNotFound(e))
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogDebug("Reading the stats of server {Uuid} failed: {Error}", Uuid, e.Message);
            }

            // the stream ends when the container stops, or broke: try again while the container is ours
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ─── Boot / delete ───────────────────────────────────────────────────────

    /// <summary>Attaches to a container that kept running while the demon was down. The state is Online, the start detection already happened.</summary>
    internal async Task ReattachAsync(DateTimeOffset? startedAt, CancellationToken cancellationToken)
    {
        await OperationLock.WaitAsync(cancellationToken);

        try
        {
            if (IsRunning)
                return;

            await LoadHistoryAsync(cancellationToken);

            var stream = await AttachAsync(cancellationToken);
            var running = new RunningContainer(stream, _shutdown) { StartedAt = startedAt ?? DateTimeOffset.UtcNow };

            lock (_stateLock)
            {
                _container = running;
                _usage = ContainerUsage.Empty;
                SetStateLocked(ServerState.Online);
            }

            running.OutputTask = Task.Run(() => PumpOutputAsync(running), CancellationToken.None);
            _ = Task.Run(() => MonitorExitAsync(running), CancellationToken.None);
            _ = Task.Run(() => MonitorStatsAsync(running), CancellationToken.None);

            _logger.LogInformation("Re-attached to the running server {Uuid} ({Name})", Uuid, Configuration.Name);
        }
        finally
        {
            OperationLock.Release();
        }
    }

    /// <summary>The last log lines of the container, so the console is not empty after a demon restart</summary>
    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var logs = await _docker.Client.Containers.GetContainerLogsAsync(ContainerName, tty: false, new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = false,
                Tail = HistoryLines.ToString()
            }, timeout.Token);

            await ContainerOutput.PumpAsync(logs, _console.Add, timeout.Token);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Could not load the console history of server {Uuid}: {Error}", Uuid, e.Message);
        }
    }

    /// <summary>Marks the server as installing (false when an installation is already running)</summary>
    internal bool TryBeginInstall(out CancellationTokenSource cancellation)
    {
        lock (_stateLock)
        {
            cancellation = null!;

            if (_installing || _restoring || _deleted)
                return false;

            _installing = true;
            _installCancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
            return true;
        }
    }

    internal void EndInstall()
    {
        lock (_stateLock)
        {
            _installing = false;
            _installCancellation?.Dispose();
            _installCancellation = null;
        }
    }

    /// <summary>Stops a running server for a reinstallation, the caller holds the operation lock</summary>
    internal Task StopForInstallAsync(CancellationToken cancellationToken) => StopLockedAsync(cancellationToken);

    /// <summary>Marks the server as restoring a backup (false while installing, restoring or deleting)</summary>
    internal bool TryBeginRestore()
    {
        lock (_stateLock)
        {
            if (_installing || _restoring || _deleted)
                return false;

            _restoring = true;
            return true;
        }
    }

    internal void EndRestore() => _restoring = false;

    /// <summary>Stops a running server for a backup restore, the caller holds the operation lock</summary>
    internal Task StopForRestoreAsync(CancellationToken cancellationToken) => StopLockedAsync(cancellationToken);

    /// <summary>Kills and removes the containers. The volume is deleted by the manager afterwards.</summary>
    internal async Task DestroyAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _deleted = true;

            try
            {
                _installCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the installation just finished
            }
        }

        try
        {
            await OperationLock.WaitAsync(cancellationToken);
        }
        catch
        {
            _deleted = false;
            throw;
        }

        try
        {
            RunningContainer? running;

            lock (_stateLock)
            {
                running = _container;

                if (running != null)
                    running.StopRequested = true;
            }

            await _docker.RemoveContainerIfExistsAsync(ContainerName, cancellationToken);
            await _docker.RemoveContainerIfExistsAsync(ContainerParameters.InstallerContainerName(Uuid), cancellationToken);

            if (running != null)
            {
                Detach(running);
                running.Exited.TrySetResult(-1);
            }

            SetState(ServerState.Offline);
        }
        catch
        {
            _deleted = false;
            throw;
        }
        finally
        {
            OperationLock.Release();
        }
    }

    /// <summary>Stops listening to the container without touching it (the container keeps running)</summary>
    internal void Detach()
    {
        RunningContainer? running;

        lock (_stateLock)
            running = _container;

        if (running != null)
        {
            running.Dispose();
            running.Exited.TrySetCanceled();
        }
    }

    private void Detach(RunningContainer running)
    {
        lock (_stateLock)
        {
            if (_container == running)
            {
                _container = null;
                _usage = ContainerUsage.Empty;
            }
        }

        running.Dispose();
    }

    /// <summary>The attached stream and the background work of one container run</summary>
    private sealed class RunningContainer : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        public RunningContainer(MultiplexedStream stream, CancellationToken shutdown)
        {
            Stream = stream;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Token = _cancellation.Token;
        }

        public MultiplexedStream Stream { get; }
        public CancellationToken Token { get; }
        public SemaphoreSlim StdinLock { get; } = new(1, 1);
        public TaskCompletionSource<long> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OutputTask { get; set; } = Task.CompletedTask;
        public DateTimeOffset? StartedAt { get; set; }

        /// <summary>Set by stop/kill/delete: the exit is expected and no crash</summary>
        public volatile bool StopRequested;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _cancellation.Cancel();
            _cancellation.Dispose();
            Stream.Dispose();
        }
    }
}
