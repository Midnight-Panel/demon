using System.Collections.Concurrent;
using Midnight.Demon.Core.Panel;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Demon.Features.Servers.Files;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Features.Servers.Backups;

/// <summary>data of the backup-completed event</summary>
public sealed record BackupCompletedEvent(Guid Uuid, bool Successful, long Bytes);

/// <summary>data of the backup-restore-completed event</summary>
public sealed record BackupRestoreCompletedEvent(bool Successful);

/// <summary>
/// Backups of server volumes in {dataPath}/backups/{uuid}/{backupUuid}.tar.gz. Creating and restoring run in the
/// background, the results are reported to the panel and broadcast on the console. Docker is only needed to stop a
/// running server before a restore.
/// </summary>
public sealed class BackupService
{
    private static readonly TimeSpan[] ReportRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly ServerFileSystem _files;
    private readonly ServerFiles _serverFiles;
    private readonly IServiceProvider _services;
    private readonly ILogger<BackupService> _logger;
    private readonly CancellationToken _stopping;

    // running backups (server, backup) and restores (backup → server)
    private readonly ConcurrentDictionary<(Guid Server, Guid Backup), CancellationTokenSource> _creating = new();
    private readonly ConcurrentDictionary<Guid, Guid> _restoring = new();

    public BackupService(ServerFileSystem files, ServerFiles serverFiles, IServiceProvider services, ILogger<BackupService> logger, IHostApplicationLifetime lifetime)
    {
        _files = files;
        _serverFiles = serverFiles;
        _services = services;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
    }

    // ─── Create ──────────────────────────────────────────────────────────────

    /// <summary>Starts a backup in the background (202), 409 when it already exists or is running</summary>
    public void Create(ServerInstance server, DemonCreateBackupRequest request)
    {
        if (request.BackupUuid == Guid.Empty)
            throw ServerApiException.BadRequest("Invalid request", "BackupUuid is required");

        var path = _files.GetBackupPath(server.Uuid, request.BackupUuid);

        if (File.Exists(path))
            throw ServerApiException.Conflict("The backup already exists");

        if (server.Restoring)
            throw ServerApiException.Conflict("A backup of the server is being restored");

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);

        if (!_creating.TryAdd((server.Uuid, request.BackupUuid), cancellation))
        {
            cancellation.Dispose();
            throw ServerApiException.Conflict("The backup is already being created");
        }

        _ = Task.Run(async () =>
        {
            DemonBackupResultRequest result;

            try
            {
                result = await CreateArchiveAsync(server, request, path, cancellation.Token);
            }
            finally
            {
                // the backup counts as finished before the (possibly retried) report
                _creating.TryRemove((server.Uuid, request.BackupUuid), out _);
                cancellation.Dispose();
            }

            // a deleted server does not exist in the panel anymore
            if (server.Deleted)
                return;

            // Report first: browsers reload the backup list from the panel when they get the event
            await ReportAsync($"backup {request.BackupUuid}", panel => panel.ReportBackupAsync(request.BackupUuid, result, CancellationToken.None));
            server.Broadcast(ConsoleEvents.BackupCompleted, new BackupCompletedEvent(request.BackupUuid, result.Successful, result.Bytes));
        }, CancellationToken.None);
    }

    /// <summary>Writes the archive, failures are logged and shown as notice (never thrown)</summary>
    private async Task<DemonBackupResultRequest> CreateArchiveAsync(ServerInstance server, DemonCreateBackupRequest request, string path, CancellationToken cancellationToken)
    {
        var result = new DemonBackupResultRequest();

        server.Notice("Creating a backup");

        try
        {
            var volume = _files.GetVolumePath(server.Uuid);
            var archive = await Task.Run(() => BackupArchive.CreateAsync(volume, path, request.IgnoredFiles, cancellationToken), cancellationToken);

            result.Successful = true;
            result.Checksum = archive.Checksum;
            result.Bytes = archive.Bytes;

            _logger.LogInformation("Backup {Backup} of server {Uuid} created: {Entries} entries, {Bytes} bytes", request.BackupUuid, server.Uuid, archive.Entries, archive.Bytes);
            server.Notice($"Backup completed ({FormatBytes(archive.Bytes)})");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
                _logger.LogWarning("Backup {Backup} of server {Uuid} was cancelled", request.BackupUuid, server.Uuid);
            else
                _logger.LogError(e, "Backup {Backup} of server {Uuid} failed", request.BackupUuid, server.Uuid);

            if (!server.Deleted)
                server.Notice(e is OperationCanceledException ? "The backup was cancelled" : $"The backup failed: {e.Message}");
        }

        return result;
    }

    // ─── Download / delete ───────────────────────────────────────────────────

    /// <summary>Path of a finished backup, 404 when it does not exist</summary>
    public string GetArchivePath(ServerInstance server, Guid backupUuid)
    {
        var path = _files.GetBackupPath(server.Uuid, backupUuid);

        if (!File.Exists(path))
        {
            if (_creating.ContainsKey((server.Uuid, backupUuid)))
                throw ServerApiException.Conflict("The backup is still being created");

            throw BackupNotFound(backupUuid);
        }

        return path;
    }

    public void Delete(ServerInstance server, Guid backupUuid)
    {
        if (_creating.ContainsKey((server.Uuid, backupUuid)))
            throw ServerApiException.Conflict("The backup is still being created");

        if (_restoring.ContainsKey(backupUuid))
            throw ServerApiException.Conflict("The backup is being restored");

        var path = _files.GetBackupPath(server.Uuid, backupUuid);

        if (!File.Exists(path))
            throw BackupNotFound(backupUuid);

        File.Delete(path);
        _logger.LogInformation("Deleted backup {Backup} of server {Uuid}", backupUuid, server.Uuid);
    }

    /// <summary>Cancels the running backups of a server (it is being deleted)</summary>
    public void CancelAll(Guid uuid)
    {
        foreach (var (key, cancellation) in _creating)
        {
            if (key.Server != uuid)
                continue;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // just finished
            }
        }
    }

    // ─── Restore ─────────────────────────────────────────────────────────────

    /// <summary>Starts a restore in the background (202): 404 unknown backup, 409 while installing, restoring or creating this backup</summary>
    public void Restore(ServerInstance server, Guid backupUuid, DemonRestoreBackupRequest request)
    {
        var path = GetArchivePath(server, backupUuid);

        if (_creating.Keys.Any(key => key.Server == server.Uuid))
            throw ServerApiException.Conflict("A backup of the server is being created");

        if (!server.TryBeginRestore())
        {
            throw ServerApiException.Conflict(server.Installing ? "The server is being installed"
                : server.Restoring ? "A backup of the server is already being restored"
                : "The server is being deleted");
        }

        _restoring[backupUuid] = server.Uuid;

        _ = Task.Run(async () =>
        {
            bool successful;

            try
            {
                successful = await RestoreArchiveAsync(server, backupUuid, path, request.Truncate);
            }
            finally
            {
                _restoring.TryRemove(backupUuid, out _);
            }

            _serverFiles.MeasureInBackground(server);

            if (server.Deleted)
                return;

            await ReportAsync($"restore of backup {backupUuid}", panel => panel.ReportRestoreAsync(backupUuid, successful, CancellationToken.None));
            server.Broadcast(ConsoleEvents.BackupRestoreCompleted, new BackupRestoreCompletedEvent(successful));
        }, CancellationToken.None);
    }

    /// <summary>Stops the server and extracts the backup while holding the operation lock. Failures are a notice, never thrown.</summary>
    private async Task<bool> RestoreArchiveAsync(ServerInstance server, Guid backupUuid, string path, bool truncate)
    {
        var successful = false;
        var lockTaken = false;

        try
        {
            await server.OperationLock.WaitAsync(_stopping);
            lockTaken = true;

            server.Broadcast(ConsoleEvents.BackupRestoreStarted, null);

            if (server.IsRunning)
            {
                server.Notice("Stopping the server to restore the backup");
                await server.StopForRestoreAsync(_stopping);

                if (server.IsRunning)
                    throw new InvalidOperationException("The server could not be stopped");
            }

            server.Notice(truncate ? "Deleting all files and restoring the backup" : "Restoring the backup");

            var volume = _files.EnsureVolume(server.Uuid);
            var result = await Task.Run(() => BackupArchive.RestoreAsync(path, volume, truncate, _serverFiles.SetOwner, _stopping), _stopping);
            _serverFiles.SetOwner(volume);

            successful = true;
            _logger.LogInformation("Restored backup {Backup} of server {Uuid}: {Entries} entries, {Bytes} bytes", backupUuid, server.Uuid, result.Entries, result.Bytes);
            server.Notice(result.Unsafe > 0
                ? $"The backup has been restored ({result.Unsafe} unsafe entries were skipped)"
                : "The backup has been restored");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException && _stopping.IsCancellationRequested)
                _logger.LogWarning("Restoring backup {Backup} of server {Uuid} was interrupted by the shutdown", backupUuid, server.Uuid);
            else
                _logger.LogError(e, "Restoring backup {Backup} of server {Uuid} failed", backupUuid, server.Uuid);

            server.Notice($"Restoring the backup failed: {DockerErrors.Describe(e)}");
        }
        finally
        {
            server.EndRestore();

            if (lockTaken)
                server.OperationLock.Release();
        }

        return successful;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Reports to the panel, retried a few times (4xx = the panel does not know the backup, not retried)</summary>
    private async Task ReportAsync(string what, Func<PanelClient, Task> report)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await report(_services.GetRequiredService<PanelClient>());
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                var rejected = e is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500;

                if (rejected || attempt >= ReportRetryDelays.Length)
                {
                    _logger.LogError("Could not report the result of the {What} to the panel: {Error}", what, e.Message);
                    return;
                }

                _logger.LogWarning("Reporting the result of the {What} failed: {Error}. Retrying", what, e.Message);

                try
                {
                    await Task.Delay(ReportRetryDelays[attempt], _stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static ServerApiException BackupNotFound(Guid backupUuid) =>
        new(StatusCodes.Status404NotFound, "Backup not found", $"The backup {backupUuid} does not exist on this node");

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MiB",
        >= 1024 => $"{bytes / 1024.0:0.##} KiB",
        _ => $"{bytes} B"
    };
}
