using System.Text;
using Docker.DotNet.Models;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Core.Panel;
using Midnight.Demon.Features.Docker;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Shared.Enums;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Features.Servers;

/// <summary>
/// Runs the install script of a server in a one-off container: volume at /mnt/server, script at /mnt/install/install.sh.
/// The output is streamed as install-output events and written to {dataPath}/logs/install/{uuid}.log.
/// </summary>
public class ServerInstaller
{
    private static readonly TimeSpan[] ReportRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly DockerService _docker;
    private readonly ServerFileSystem _files;
    private readonly ServerRuntimeConfiguration _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<ServerInstaller> _logger;

    public ServerInstaller(DockerService docker, ServerFileSystem files, DemonConfiguration configuration, IServiceProvider services, ILogger<ServerInstaller> logger)
    {
        _docker = docker;
        _files = files;
        _options = configuration.Servers;
        _services = services;
        _logger = logger;
    }

    /// <summary>Starts an installation, false when one is already running</summary>
    internal bool TryBegin(ServerInstance server, out CancellationTokenSource cancellation) => server.TryBeginInstall(out cancellation);

    /// <summary>
    /// Installs the server (after <see cref="TryBegin"/>), reports the result to the panel and starts the server
    /// when <paramref name="startOnCompletion"/> is set. Never throws except on cancellation.
    /// </summary>
    internal async Task RunAsync(ServerInstance server, CancellationTokenSource cancellation, bool startOnCompletion)
    {
        var token = cancellation.Token;
        var successful = false;
        var lockTaken = false;

        try
        {
            await server.OperationLock.WaitAsync(token);
            lockTaken = true;

            if (server.IsRunning)
            {
                server.Notice("Stopping the server for the installation");
                await server.StopForInstallAsync(token);
            }

            server.Broadcast(ConsoleEvents.InstallStarted, null);
            server.SetState(ServerState.Installing);

            successful = await InstallAsync(server, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogWarning("The installation of server {Uuid} was cancelled", server.Uuid);
        }
        catch (Exception e)
        {
            // An unreachable docker engine is a known condition, the stack trace would only add noise
            if (DockerErrors.IsUnavailable(e))
                _logger.LogError("The installation of server {Uuid} failed: {Error}", server.Uuid, DockerErrors.Describe(e));
            else
                _logger.LogError(e, "The installation of server {Uuid} failed", server.Uuid);
            server.Notice($"The installation failed: {DockerErrors.Describe(e)}");
        }
        finally
        {
            server.EndInstall();

            if (lockTaken)
            {
                server.Broadcast(ConsoleEvents.InstallCompleted, successful);
                server.SetState(ServerState.Offline);
                server.OperationLock.Release();
            }
        }

        // a deleted server does not exist in the panel anymore
        if (server.Deleted)
            return;

        _logger.LogInformation("Installation of server {Uuid} {Result}", server.Uuid, successful ? "succeeded" : "failed");
        await ReportAsync(server.Uuid, successful);

        if (successful && startOnCompletion)
        {
            var refusal = await server.CheckPowerAsync(PowerAction.Start, CancellationToken.None);

            if (refusal != null)
                server.Notice(refusal);
            else
                await server.PowerAsync(PowerAction.Start, CancellationToken.None);
        }
    }

    private async Task<bool> InstallAsync(ServerInstance server, CancellationToken cancellationToken)
    {
        var configuration = server.Configuration;
        var volume = _files.EnsureVolume(server.Uuid);

        Directory.CreateDirectory(_files.InstallLogsRoot);
        using var log = new InstallLog(_files.GetInstallLogPath(server.Uuid));

        log.Write($"Midnight install log of server {configuration.Name} ({server.Uuid}), {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        log.Write($"Image: {configuration.Install.Image}, interpreter: {configuration.Install.Interpreter}");
        log.Write("");

        if (string.IsNullOrWhiteSpace(configuration.Install.Script))
        {
            log.Write("No install script, nothing to do.");
            _files.FixPermissions(volume);
            return true;
        }

        var scriptDirectory = _files.GetInstallScriptDirectory(server.Uuid);
        var containerName = ContainerParameters.InstallerContainerName(server.Uuid);

        void Output(string line)
        {
            log.Write(line);
            server.Broadcast(ConsoleEvents.InstallOutput, line);
        }

        try
        {
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = Path.Combine(scriptDirectory, ContainerParameters.InstallScriptName);

            // scripts edited on windows would fail with "bad interpreter: /bin/bash^M"
            var script = configuration.Install.Script.Replace("\r\n", "\n").Replace('\r', '\n');
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), cancellationToken);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                 UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            await _docker.EnsureImageAsync(configuration.Install.Image, Output, cancellationToken);

            await _docker.RemoveContainerIfExistsAsync(containerName, cancellationToken);
            await _docker.Client.Containers.CreateContainerAsync(ContainerParameters.ForInstaller(configuration, volume, scriptDirectory, _options), cancellationToken);

            using var stream = await _docker.Client.Containers.AttachContainerAsync(containerName, tty: false, new ContainerAttachParameters
            {
                Stream = true,
                Stdout = true,
                Stderr = true
            }, cancellationToken);

            var output = Task.Run(async () =>
            {
                try
                {
                    await ContainerOutput.PumpAsync(stream, Output, cancellationToken);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // the stream is disposed when the installation ends or is cancelled
                }
            }, CancellationToken.None);

            await _docker.Client.Containers.StartContainerAsync(containerName, new ContainerStartParameters(), cancellationToken);
            var result = await _docker.Client.Containers.WaitContainerAsync(containerName, cancellationToken);

            try
            {
                await output.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // the remaining output is lost, the exit code is what counts
            }

            log.Write("");
            log.Write($"The install script exited with code {result.StatusCode}");
            server.Broadcast(ConsoleEvents.InstallOutput, $"The install script exited with code {result.StatusCode}");

            if (result.StatusCode != 0)
                return false;

            await Task.Run(() => _files.FixPermissions(volume), CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Write("");
            log.Write($"The installation failed: {DockerErrors.Describe(e)}");
            throw;
        }
        finally
        {
            await CleanupAsync(containerName, scriptDirectory);
        }
    }

    private async Task CleanupAsync(string containerName, string scriptDirectory)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _docker.RemoveContainerIfExistsAsync(containerName, timeout.Token);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Could not remove the install container {Name}: {Error}", containerName, DockerErrors.Describe(e));
        }

        try
        {
            if (Directory.Exists(scriptDirectory))
                Directory.Delete(scriptDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete the install script directory {Path}: {Error}", scriptDirectory, e.Message);
        }
    }

    /// <summary>Tells the panel the result, retried a few times because the server stays "Installing" otherwise</summary>
    internal async Task ReportAsync(Guid uuid, bool successful)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _services.GetRequiredService<PanelClient>().ReportInstallAsync(uuid, successful, CancellationToken.None);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // 4xx: the panel does not know the server (anymore), asking again won't help
                var rejected = e is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500;

                if (rejected || attempt >= ReportRetryDelays.Length)
                {
                    _logger.LogError("Could not report the installation result of server {Uuid} to the panel: {Error}", uuid, e.Message);
                    return;
                }

                _logger.LogWarning("Reporting the installation result of server {Uuid} failed: {Error}. Retrying", uuid, e.Message);
                await Task.Delay(ReportRetryDelays[attempt]);
            }
        }
    }

    /// <summary>Thread safe install log file, writes after dispose are ignored</summary>
    private sealed class InstallLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Lock _lock = new();
        private bool _disposed;

        public InstallLog(string path)
        {
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public void Write(string line)
        {
            lock (_lock)
            {
                if (!_disposed)
                    _writer.WriteLine(line);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _writer.Dispose();
            }
        }
    }
}
