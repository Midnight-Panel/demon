using Midnight.Demon.Core.Configuration;

namespace Midnight.Demon.Features.Servers;

/// <summary>Measures the disk usage of every server volume every Servers.DiskCheckIntervalSeconds</summary>
public sealed class DiskUsageService(ServerManager manager, DemonConfiguration configuration, ILogger<DiskUsageService> logger) : BackgroundService
{
    private const long MiB = 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.Servers.DiskCheckIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var server in manager.All)
            {
                if (server.Deleted)
                    continue;

                try
                {
                    var previous = server.DiskBytes;
                    var size = await server.MeasureDiskAsync(stoppingToken);
                    var limit = server.Configuration.Limits.Disk * MiB;

                    // tell the users once when a running server grows over its limit, it can't be started again until they clean up
                    if (limit > 0 && size > limit && previous <= limit && server.IsRunning)
                        server.Notice($"The server uses {size / MiB} MiB of its {limit / MiB} MiB disk space, it can't be started again until files are deleted");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogWarning("Could not measure the disk usage of server {Uuid}: {Error}", server.Uuid, e.Message);
                }
            }
        }
    }
}
