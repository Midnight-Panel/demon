using Midnight.Daemon.Core.Panel;

namespace Midnight.Daemon.Features.Host;

/// <summary>Fetches the servers of this node from the panel on start. Retries with backoff while the panel is unreachable.</summary>
public class BootSyncService : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);

    private readonly PanelClient _panel;
    private readonly ILogger<BootSyncService> _logger;

    public BootSyncService(PanelClient panel, ILogger<BootSyncService> logger)
    {
        _panel = panel;
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

                // TODO: Phase 3 - create missing containers, remove containers of deleted servers
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Boot sync failed: {Error}. Retrying in {Delay}s", e.Message, (int)delay.TotalSeconds);

                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
            }
        }
    }
}
