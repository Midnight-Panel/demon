namespace Midnight.Demon.Features.Servers.Containers;

/// <summary>Splits "ghcr.io/org/image:tag" into the repository and tag the docker pull api expects</summary>
internal readonly record struct ImageReference(string Repository, string? Tag)
{
    public static ImageReference Parse(string image)
    {
        var value = image.Trim();

        // pinned by digest: repository@sha256:...
        if (value.Contains('@'))
            return new ImageReference(value, null);

        var lastSlash = value.LastIndexOf('/');
        var lastColon = value.LastIndexOf(':');

        // a colon before the last slash belongs to a registry port (localhost:5000/image)
        if (lastColon > lastSlash && lastColon < value.Length - 1)
            return new ImageReference(value[..lastColon], value[(lastColon + 1)..]);

        return new ImageReference(value.TrimEnd(':'), "latest");
    }
}
