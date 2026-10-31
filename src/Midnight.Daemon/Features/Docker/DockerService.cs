using Docker.DotNet;
using Midnight.Daemon.Core.Configuration;
using Midnight.Shared.Http.Daemon;

namespace Midnight.Daemon.Features.Docker;

/// <summary>Connection to the docker engine of the node</summary>
public class DockerService : IDisposable
{
    private readonly ILogger<DockerService> _logger;

    public DockerClient Client { get; }

    public DockerService(DaemonConfiguration configuration, ILogger<DockerService> logger)
    {
        _logger = logger;
        Client = new DockerClientConfiguration(new Uri(configuration.DockerSocket), defaultTimeout: TimeSpan.FromSeconds(10)).CreateClient();
    }

    /// <summary>Never throws, an unreachable engine is reported as unavailable</summary>
    public async Task<DockerInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await Client.System.GetVersionAsync(cancellationToken);
            var info = await Client.System.GetSystemInfoAsync(cancellationToken);

            return new DockerInfo
            {
                Available = true,
                Version = version.Version,
                Containers = (int)info.Containers,
                ContainersRunning = (int)info.ContainersRunning,
                Images = (int)info.Images
            };
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(e, "Docker engine is not reachable");
            return new DockerInfo { Available = false, Error = e.Message };
        }
    }

    public void Dispose() => Client.Dispose();
}
