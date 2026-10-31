using System.Collections.Concurrent;
using Docker.DotNet.Models;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Features.Docker;
using Midnight.Demon.Features.Servers.Backups;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Shared.Enums;
using Midnight.Shared.Http.Demon;

namespace Midnight.Demon.Features.Servers;

/// <summary>
/// In-memory registry of the servers of this node. The panel is the source of truth: the registry is filled on boot
/// (GET /api/nodes/remote/servers) and kept up to date by the api. Only volumes and install logs are stored on disk.
/// </summary>
public sealed class ServerManager
{
    private readonly ConcurrentDictionary<Guid, ServerInstance> _servers = new();
    private readonly DockerService _docker;
    private readonly ServerFileSystem _files;
    private readonly ServerInstaller _installer;
    private readonly BackupService _backups;
    private readonly DemonConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServerManager> _logger;
    private readonly CancellationToken _stopping;

    public ServerManager(DockerService docker, ServerFileSystem files, ServerInstaller installer, BackupService backups, DemonConfiguration configuration,
        ILoggerFactory loggerFactory, IHostApplicationLifetime lifetime)
    {
        _backups = backups;
        _docker = docker;
        _files = files;
        _installer = installer;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ServerManager>();
        _stopping = lifetime.ApplicationStopping;

        // containers keep running while the demon is down, only the streams are closed
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var server in _servers.Values)
                server.Detach();
        });
    }

    public IReadOnlyList<ServerInstance> All => _servers.Values.OrderBy(x => x.Configuration.Name).ToList();

    public ServerInstance? Get(Guid uuid) => _servers.GetValueOrDefault(uuid);

    public ServerInstance GetRequired(Guid uuid) => Get(uuid) ?? throw ServerApiException.NotFound(uuid);

    // ─── Api operations ──────────────────────────────────────────────────────

    /// <summary>Registers a new server and installs it in the background</summary>
    public ServerInstance Create(DemonCreateServerRequest request)
    {
        Validate(request.Configuration);

        var server = CreateInstance(request.Configuration);

        if (!_servers.TryAdd(server.Uuid, server))
            throw ServerApiException.Conflict($"The server {server.Uuid} already exists on this node");

        _files.EnsureVolume(server.Uuid);
        _logger.LogInformation("Created server {Uuid} ({Name})", server.Uuid, request.Configuration.Name);

        if (request.SkipInstall)
        {
            if (request.StartOnCompletion)
                RunInBackground(server, "Start", token => server.PowerAsync(PowerAction.Start, token));
        }
        else
        {
            BeginInstall(server, request.StartOnCompletion);
        }

        return server;
    }

    public async Task UpdateAsync(Guid uuid, DemonServerConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.Uuid == Guid.Empty)
            configuration.Uuid = uuid;

        if (configuration.Uuid != uuid)
            throw ServerApiException.BadRequest("Invalid server configuration", "The uuid of the configuration does not match the url");

        Validate(configuration);

        await GetRequired(uuid).UpdateConfigurationAsync(configuration, cancellationToken);
    }

    public void Reinstall(Guid uuid)
    {
        var server = GetRequired(uuid);

        if (!BeginInstall(server, startOnCompletion: false))
            throw ServerApiException.Conflict(server.Restoring ? "A backup of the server is being restored" : "The server is already being installed");
    }

    /// <summary>Checks the action and runs it in the background. Refusals are a notice event and a 409.</summary>
    public async Task PowerAsync(Guid uuid, PowerAction action, CancellationToken cancellationToken)
    {
        var server = GetRequired(uuid);
        var refusal = await server.CheckPowerAsync(action, cancellationToken);

        if (refusal != null)
        {
            server.Notice(refusal);
            throw ServerApiException.Conflict(refusal);
        }

        RunPower(server, action);
    }

    /// <summary>Runs a checked power action in the background</summary>
    internal void RunPower(ServerInstance server, PowerAction action) =>
        RunInBackground(server, action.ToString(), token => server.PowerAsync(action, token));

    /// <summary>Kills the server, removes its containers and deletes the volume and the backups</summary>
    public async Task DeleteAsync(Guid uuid, CancellationToken cancellationToken)
    {
        var server = GetRequired(uuid);

        await server.DestroyAsync(cancellationToken);

        _servers.TryRemove(uuid, out _);
        server.CompleteSubscriptions();
        _backups.CancelAll(uuid);

        try
        {
            await Task.Run(() =>
            {
                _files.DeleteVolume(uuid);
                _files.DeleteInstallFiles(uuid);
                _files.DeleteBackups(uuid);
            }, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Could not delete the files of server {Uuid}", uuid);
            throw new ServerApiException(StatusCodes.Status500InternalServerError, "The server files could not be deleted", e.Message);
        }

        _logger.LogInformation("Deleted server {Uuid} ({Name})", uuid, server.Configuration.Name);
    }

    // ─── Boot ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the servers of the panel, re-attaches to containers that are still running and cleans up containers of
    /// servers the panel does not know anymore and install containers of interrupted installations.
    /// False when the docker engine could not be reached (the servers are registered, calling it again is safe).
    /// </summary>
    public async Task<bool> LoadAsync(IReadOnlyCollection<DemonServerConfiguration> configurations, CancellationToken cancellationToken)
    {
        foreach (var configuration in configurations)
        {
            var errors = ServerConfigurationValidator.Validate(configuration);

            if (errors.Count > 0)
            {
                _logger.LogWarning("Ignoring server {Uuid} from the panel: {Errors}", configuration.Uuid, string.Join("; ", errors));
                continue;
            }

            if (_servers.TryGetValue(configuration.Uuid, out var existing))
                await existing.UpdateConfigurationAsync(configuration, cancellationToken);
            else
                _servers.TryAdd(configuration.Uuid, CreateInstance(configuration));
        }

        _logger.LogInformation("Loaded {Count} server(s) from the panel, {Total} registered on this node", configurations.Count, _servers.Count);

        IList<ContainerListResponse> containers;

        try
        {
            containers = await _docker.Client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [ContainerParameters.ServerLabel] = true }
                }
            }, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Could not list the server containers, running servers are not re-attached yet: {Error}", DockerErrors.Describe(e));
            return false;
        }

        foreach (var container in containers)
        {
            try
            {
                await RestoreContainerAsync(container, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Could not restore container {Name}: {Error}", container.Names?.FirstOrDefault() ?? container.ID, DockerErrors.Describe(e));
            }
        }

        // the disk usage is known right away instead of after the first interval
        foreach (var server in _servers.Values)
            RunInBackground(server, "Measuring the disk", server.MeasureDiskAsync);

        return true;
    }

    private async Task RestoreContainerAsync(ContainerListResponse container, CancellationToken cancellationToken)
    {
        if (container.Labels == null ||
            !container.Labels.TryGetValue(ContainerParameters.ServerLabel, out var label) ||
            !Guid.TryParse(label, out var uuid))
            return;

        container.Labels.TryGetValue(ContainerParameters.TypeLabel, out var type);
        var server = Get(uuid);

        if (type == ContainerParameters.InstallerType)
        {
            // the demon stopped during an installation, the panel still shows "Installing"
            _logger.LogWarning("Removing the install container of server {Uuid} left from an interrupted installation", uuid);
            await _docker.RemoveContainerIfExistsAsync(container.ID, cancellationToken);

            if (server != null)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _installer.ReportAsync(uuid, successful: false);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Could not report the interrupted installation of server {Uuid}", uuid);
                    }
                }, CancellationToken.None);

            return;
        }

        if (server == null)
        {
            _logger.LogWarning("Removing the container of server {Uuid}, the panel does not know this server anymore (the volume is kept)", uuid);
            await _docker.RemoveContainerIfExistsAsync(container.ID, cancellationToken);
            return;
        }

        if (container.State != "running")
            return;

        var inspect = await _docker.Client.Containers.InspectContainerAsync(container.ID, cancellationToken);
        DateTimeOffset? startedAt = DateTimeOffset.TryParse(inspect.State?.StartedAt, out var started) ? started : null;

        await server.ReattachAsync(startedAt, cancellationToken);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private ServerInstance CreateInstance(DemonServerConfiguration configuration) =>
        new(configuration, _docker, _files, _configuration.Servers, _loggerFactory.CreateLogger<ServerInstance>(), _stopping);

    private bool BeginInstall(ServerInstance server, bool startOnCompletion)
    {
        if (!_installer.TryBegin(server, out var cancellation))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await _installer.RunAsync(server, cancellation, startOnCompletion);
            }
            catch (Exception e)
            {
                // RunAsync handles the expected errors itself, this only keeps exceptions from going unobserved
                _logger.LogError(e, "The installation of server {Uuid} failed", server.Uuid);
            }
        }, CancellationToken.None);
        return true;
    }

    /// <summary>Runs work of a server in the background, failures are logged and shown as notice (no unobserved exceptions)</summary>
    private void RunInBackground(ServerInstance server, string operation, Func<CancellationToken, Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work(_stopping);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // the demon shuts down
            }
            catch (Exception e)
            {
                _logger.LogError(e, "{Operation} of server {Uuid} failed", operation, server.Uuid);
                server.Notice($"{operation} failed: {DockerErrors.Describe(e)}");
            }
        }, CancellationToken.None);
    }

    private static void Validate(DemonServerConfiguration configuration)
    {
        var errors = ServerConfigurationValidator.Validate(configuration);

        if (errors.Count > 0)
            throw ServerApiException.BadRequest("Invalid server configuration", string.Join("; ", errors));
    }
}
