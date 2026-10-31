using Docker.DotNet;
using Docker.DotNet.Models;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Shared.Http.Demon;

namespace Midnight.Demon.Features.Docker;

/// <summary>Connection to the docker engine of the node</summary>
public class DockerService : IDisposable
{
    private readonly ILogger<DockerService> _logger;

    public DockerClient Client { get; }

    public DockerService(DemonConfiguration configuration, ILogger<DockerService> logger)
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

    /// <summary>Pulls the image when it is not available locally. <paramref name="onStatus"/> gets the pull status lines (without progress bars).</summary>
    public async Task EnsureImageAsync(string image, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException("No docker image configured");

        try
        {
            await Client.Images.InspectImageAsync(image, cancellationToken);
            return;
        }
        catch (Exception e) when (DockerErrors.IsNotFound(e))
        {
            // pull below
        }

        _logger.LogInformation("Pulling image {Image}", image);
        onStatus?.Invoke($"Pulling image {image}");

        var reference = ImageReference.Parse(image);
        string? error = null;

        // synchronous progress: Progress<T> would post to the thread pool and report after the call returned
        var progress = new InlineProgress<JSONMessage>(message =>
        {
            if (message.Error != null || !string.IsNullOrEmpty(message.ErrorMessage))
                error = message.Error?.Message ?? message.ErrorMessage;
            else if (message.Progress == null && !string.IsNullOrEmpty(message.Status))
                onStatus?.Invoke(string.IsNullOrEmpty(message.ID) ? message.Status : $"{message.ID}: {message.Status}");
        });

        await Client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = reference.Repository, Tag = reference.Tag }, null, progress, cancellationToken);

        if (error != null)
            throw new InvalidOperationException($"Pulling {image} failed: {error}");

        _logger.LogInformation("Pulled image {Image}", image);
    }

    /// <summary>Removes (and kills) a container, a missing container is fine</summary>
    public async Task RemoveContainerIfExistsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await Client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (Exception e) when (DockerErrors.IsNotFound(e))
        {
            // already gone
        }
    }

    /// <summary>Sends a signal to a container. False when it does not exist or does not run.</summary>
    public async Task<bool> SignalAsync(string name, string signal, CancellationToken cancellationToken)
    {
        try
        {
            await Client.Containers.KillContainerAsync(name, new ContainerKillParameters { Signal = signal }, cancellationToken);
            return true;
        }
        catch (Exception e) when (DockerErrors.IsNotFound(e) || DockerErrors.IsConflict(e))
        {
            return false;
        }
    }

    public void Dispose() => Client.Dispose();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
