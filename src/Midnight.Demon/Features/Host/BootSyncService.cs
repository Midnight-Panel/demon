using Midnight.Demon.Core.Panel;
using Midnight.Demon.Features.Servers;

namespace Midnight.Demon.Features.Host;

/// <summary>
/// On start: fetches the servers of this node from the panel, registers them and re-attaches to the containers that
/// kept running. Retries with backoff while the panel or the docker engine is unreachable.
/// </summary>
public class BootSyncService : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);

    private readonly PanelClient _panel;
    private readonly ServerManager _servers;
    private readonly ILogger<BootSyncService> _logger;

    public BootSyncService(PanelClient panel, ServerManager servers, ILogger<BootSyncService> logger)
    {
        _panel = panel;
        _servers = servers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var boot = await _panel.GetBootAsync(stoppingToken);

                _logger.LogInformation("Connected to the panel as node '{NodeName}', {Count} server(s) assigned", boot.NodeName, boot.Servers.Count);

                var configurations = await _panel.GetServersAsync(stoppingToken);

                if (await _servers.LoadAsync(configurations, stoppingToken))
                    return;

                _logger.LogWarning("The docker engine is not reachable, retrying the server sync in {Delay}s", (int)delay.TotalSeconds);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Boot sync failed: {Error}. Retrying in {Delay}s", e.Message, (int)delay.TotalSeconds);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(e, "Boot sync failed unexpectedly. Retrying in {Delay}s", (int)delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
        }
    }
}
