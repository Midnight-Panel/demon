namespace Midnight.Demon.Features.Servers.Files;

/// <summary>Names of copies, archives and uploaded or downloaded files</summary>
public static class FileNames
{
    /// <summary>Extensions that consist of two parts and stay together when a copy is named</summary>
    private static readonly string[] DoubleExtensions = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"];

    /// <summary>
    /// The first free name for a copy next to the original: "name copy.ext", "name copy 2.ext", "name copy 3.ext", ...
    /// <paramref name="exists"/> tells whether a name is taken.
    /// </summary>
    public static string GetCopyName(string fileName, Func<string, bool> exists)
    {
        var (stem, extension) = SplitExtension(fileName);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = i == 1 ? $"{stem} copy{extension}" : $"{stem} copy {i}{extension}";

            if (!exists(candidate))
                return candidate;
        }

        throw ServerApiException.Conflict("There are too many copies of this file");
    }

    /// <summary>"server.properties" → ("server", ".properties"), "a.tar.gz" → ("a", ".tar.gz"), ".env" → (".env", "")</summary>
    public static (string Stem, string Extension) SplitExtension(string fileName)
    {
        foreach (var extension in DoubleExtensions)
        {
            if (fileName.Length > extension.Length && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return (fileName[..^extension.Length], fileName[^extension.Length..]);
        }

        var dot = fileName.LastIndexOf('.');

        // no extension or a dotfile like ".env"
        if (dot <= 0)
            return (fileName, "");

        return (fileName[..dot], fileName[dot..]);
    }

    /// <summary>"archive-2026-10-31T120000Z.tar.gz", a counter is added when the name is taken</summary>
    public static string GetArchiveName(DateTimeOffset now, string extension, Func<string, bool> exists)
    {
        var baseName = $"archive-{now.UtcDateTime:yyyy-MM-dd'T'HHmmss}Z";
        var name = baseName + extension;

        for (var i = 2; exists(name); i++)
            name = $"{baseName}-{i}{extension}";

        return name;
    }

    /// <summary>
    /// A single file name from user input (upload part names, Content-Disposition, url segments): directories are
    /// dropped, control characters removed. Null when nothing usable is left.
    /// </summary>
    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var last = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var cleaned = new string(last.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (cleaned is "" or "." or ".." || cleaned.Length > 255)
            return null;

        return cleaned;
    }
}
