using System.Formats.Tar;
using System.IO.Compression;

namespace Midnight.Demon.Features.Servers.Files;

public enum ArchiveFormat
{
    Zip,
    Tar,
    TarGz,
    Gz
}

public sealed class ExtractOptions
{
    /// <summary>Most bytes the extracted files may have together (disk limit), exceeding it aborts with <see cref="SizeLimitExceededException"/></summary>
    public long MaxBytes { get; init; } = long.MaxValue;

    /// <summary>(path relative to the destination, is directory) → true = don't extract (e.g. the denylist)</summary>
    public Func<string, bool, bool>? Skip { get; init; }

    /// <summary>Called with the full path of every created file, directory or link (ownership for the container user)</summary>
    public Action<string>? Created { get; init; }
}

public sealed record ExtractResult(long Bytes, int Entries, int Unsafe, int Skipped);

/// <summary>
/// Extracts zip, tar, tar.gz and gz archives. Every entry must stay inside of the destination (zip slip): entries
/// leaving it, absolute or escaping link targets, hard links and device files are not extracted but counted as unsafe.
/// Existing files are overwritten, links in the way are replaced instead of written through. Sizes are counted from
/// the real data, not from the (forgeable) headers.
/// </summary>
public static class ArchiveExtractor
{
    private const int UserReadWrite = 0b110_000_000;

    /// <summary>The format by the file name, then by the first bytes. Null = not a supported archive.</summary>
    public static ArchiveFormat? DetectFormat(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        if (name.EndsWith(".zip"))
            return ArchiveFormat.Zip;

        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz"))
            return ArchiveFormat.TarGz;

        if (name.EndsWith(".tar"))
            return ArchiveFormat.Tar;

        if (name.EndsWith(".gz"))
            return ArchiveFormat.Gz;

        Span<byte> header = stackalloc byte[512];
        int read;

        using (var stream = File.OpenRead(path))
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        if (read >= 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 3 && header[3] == 4)
            return ArchiveFormat.Zip;

        if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B)
            return name.Contains(".tar") ? ArchiveFormat.TarGz : ArchiveFormat.Gz;

        if (read >= 262 && header[257..262].SequenceEqual("ustar"u8))
            return ArchiveFormat.Tar;

        return null;
    }

    public static async Task<ExtractResult> ExtractAsync(string archivePath, ArchiveFormat format, string destination, ExtractOptions options, CancellationToken cancellationToken)
    {
        var context = new Context(Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar), options);
        Directory.CreateDirectory(context.Destination);

        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        switch (format)
        {
            case ArchiveFormat.Zip:
                await ExtractZipAsync(file, context, cancellationToken);
                break;
            case ArchiveFormat.Tar:
                await ExtractTarAsync(file, context, cancellationToken);
                break;
            case ArchiveFormat.TarGz:
                await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                    await ExtractTarAsync(gzip, context, cancellationToken);
                break;
            case ArchiveFormat.Gz:
            {
                var name = Path.GetFileName(archivePath);
                name = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && name.Length > 3 ? name[..^3] : name + ".out";

                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await ExtractFileAsync(context, name, gzip, null, cancellationToken);
                break;
            }
        }

        return new ExtractResult(context.Bytes, context.Entries, context.Unsafe, context.Skipped);
    }

    private static async Task ExtractZipAsync(Stream stream, Context context, CancellationToken cancellationToken)
    {
        await using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var type = unixMode & 0xF000;

            // symlinks (and other special files) stored by unix zip tools: never extracted
            if (type != 0 && type != 0x8000 && type != 0x4000)
            {
                context.Unsafe++;
                continue;
            }

            var mode = unixMode & 0x1FF;

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || type == 0x4000)
            {
                CreateDirectory(context, entry.FullName, mode);
                continue;
            }

            await using var data = await entry.OpenAsync(cancellationToken);
            await ExtractFileAsync(context, entry.FullName, data, mode == 0 ? null : mode, cancellationToken);
        }
    }

    private static async Task ExtractTarAsync(Stream stream, Context context, CancellationToken cancellationToken)
    {
        await using var tar = new TarReader(stream, leaveOpen: true);

        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    CreateDirectory(context, entry.Name, (int)entry.Mode & 0x1FF);
                    break;

                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    if (entry.DataStream == null)
                        await ExtractFileAsync(context, entry.Name, Stream.Null, (int)entry.Mode & 0x1FF, cancellationToken);
                    else
                        await ExtractFileAsync(context, entry.Name, entry.DataStream, (int)entry.Mode & 0x1FF, cancellationToken);
                    break;

                case TarEntryType.SymbolicLink:
                    CreateSymlink(context, entry.Name, entry.LinkName);
                    break;

                case TarEntryType.GlobalExtendedAttributes:
                    // metadata, nothing to extract
                    break;

                default:
                    // hard links, devices, fifos, ...
                    context.Unsafe++;
                    break;
            }
        }
    }

    /// <summary>Full path of an entry inside of the destination, null (counted as unsafe) when it would leave it</summary>
    private static string? Resolve(Context context, string name, out string relative)
    {
        relative = name.Replace('\\', '/').Trim('/');

        // "./" and "." are the destination itself
        while (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];

        if (relative is "" or ".")
            return null;

        try
        {
            return ServerFileSystem.ResolveInside(context.Destination, relative);
        }
        catch (UnauthorizedAccessException)
        {
            context.Unsafe++;
            return null;
        }
    }

    private static void CreateDirectory(Context context, string name, int mode)
    {
        var full = Resolve(context, name, out var relative);

        if (full == null)
            return;

        if (context.Options.Skip?.Invoke(relative, true) == true)
        {
            context.Skipped++;
            return;
        }

        RemoveLink(full);

        if (File.Exists(full))
        {
            // a file is in the way, keep it
            context.Skipped++;
            return;
        }

        EnsureDirectory(context, full);
        SetMode(full, mode == 0 ? null : mode | 0b111_000_000);
        context.Entries++;
    }

    private static async Task ExtractFileAsync(Context context, string name, Stream data, int? mode, CancellationToken cancellationToken)
    {
        var full = Resolve(context, name, out var relative);

        if (full == null)
            return;

        if (context.Options.Skip?.Invoke(relative, false) == true || Directory.Exists(full) && new FileInfo(full).LinkTarget == null)
        {
            context.Skipped++;
            return;
        }

        EnsureDirectory(context, Path.GetDirectoryName(full)!);
        RemoveLink(full);

        // replaced, not opened: an existing fifo would block the write
        if (File.Exists(full))
            File.Delete(full);

        try
        {
            await using (var output = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await data.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (context.Bytes + read > context.Options.MaxBytes)
                        throw new SizeLimitExceededException("Extracting the archive would exceed the disk limit of the server");

                    context.Bytes += read;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        catch
        {
            TryDelete(full);
            throw;
        }

        SetMode(full, mode is null or 0 ? null : mode.Value | UserReadWrite);
        context.Options.Created?.Invoke(full);
        context.Entries++;
    }

    private static void CreateSymlink(Context context, string name, string target)
    {
        var full = Resolve(context, name, out var relative);

        if (full == null)
            return;

        if (context.Options.Skip?.Invoke(relative, false) == true)
        {
            context.Skipped++;
            return;
        }

        // absolute targets point to the host, relative ones must stay inside of the destination
        if (string.IsNullOrEmpty(target) || target.Contains('\0') || Path.IsPathRooted(target) || target.StartsWith('/'))
        {
            context.Unsafe++;
            return;
        }

        var parent = Path.GetDirectoryName(full)!;
        var resolvedTarget = Path.GetFullPath(target.Replace('/', Path.DirectorySeparatorChar), parent);

        if (!ServerFileSystem.IsInside(context.Destination, resolvedTarget) ||
            !ServerFileSystem.IsInside(ServerFileSystem.GetRealPath(context.Destination), ServerFileSystem.GetRealPath(resolvedTarget)))
        {
            context.Unsafe++;
            return;
        }

        EnsureDirectory(context, parent);

        if (Directory.Exists(full) && new FileInfo(full).LinkTarget == null)
        {
            context.Skipped++;
            return;
        }

        RemoveLink(full);

        if (File.Exists(full))
            File.Delete(full);

        File.CreateSymbolicLink(full, target);
        context.Options.Created?.Invoke(full);
        context.Entries++;
    }

    /// <summary>Creates the missing directories of the path one by one, so each new one can be handed to <see cref="ExtractOptions.Created"/></summary>
    private static void EnsureDirectory(Context context, string directory)
    {
        if (Directory.Exists(directory))
            return;

        var missing = new Stack<string>();

        for (var current = directory; !Directory.Exists(current) && ServerFileSystem.IsInside(context.Destination, current); current = Path.GetDirectoryName(current)!)
            missing.Push(current);

        while (missing.TryPop(out var path))
        {
            RemoveLink(path);
            Directory.CreateDirectory(path);
            context.Options.Created?.Invoke(path);
        }
    }

    /// <summary>A link where the entry goes is replaced, never written through</summary>
    private static void RemoveLink(string path)
    {
        var info = new FileInfo(path);

        if (info.LinkTarget != null)
            info.Delete();
    }

    private static void SetMode(string path, int? mode)
    {
        if (mode == null || OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, (UnixFileMode)mode.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the default mode is fine
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed class Context(string destination, ExtractOptions options)
    {
        public string Destination { get; } = destination;
        public ExtractOptions Options { get; } = options;
        public long Bytes;
        public int Entries;
        public int Unsafe;
        public int Skipped;
    }
}
