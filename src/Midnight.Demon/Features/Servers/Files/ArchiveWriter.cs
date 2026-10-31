using System.Formats.Tar;
using System.IO.Compression;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>
/// Writes tar.gz and zip archives of directory trees. Symlinks are never followed: tar stores them as link entries,
/// zip skips them. Paths in the archive are relative to the root with "/" separators.
/// </summary>
public static class ArchiveWriter
{
    /// <summary>A file system entry below the root, found by <see cref="Walk"/></summary>
    public readonly record struct Entry(string RelativePath, FileSystemInfo Info, bool IsDirectory, bool IsLink);

    /// <summary>
    /// The given paths (relative to <paramref name="root"/>, "" = the root itself) and everything below them, top down.
    /// <paramref name="exclude"/> (relative path, is directory) skips entries, a skipped directory is not entered.
    /// Links are returned as entries but never entered.
    /// </summary>
    public static IEnumerable<Entry> Walk(string root, IEnumerable<string> paths, Func<string, bool, bool>? exclude = null)
    {
        var fullRoot = Path.GetFullPath(root);
        var pending = new Stack<Entry>();

        foreach (var path in paths.Reverse())
        {
            var relative = path.Replace('\\', '/').Trim('/');

            if (relative.Length == 0)
            {
                foreach (var child in Children(fullRoot, ""))
                    pending.Push(child);

                continue;
            }

            var full = Path.Combine(fullRoot, relative);
            var info = ToEntry(full, relative);

            if (info != null)
                pending.Push(info.Value);
        }

        while (pending.TryPop(out var entry))
        {
            if (exclude != null && exclude(entry.RelativePath, entry.IsDirectory))
                continue;

            yield return entry;

            if (entry.IsDirectory && !entry.IsLink)
            {
                foreach (var child in Children(entry.Info.FullName, entry.RelativePath))
                    pending.Push(child);
            }
        }

        // children are pushed in reverse so they come out sorted
        static IEnumerable<Entry> Children(string directory, string relative)
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            var children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options)
                .OrderByDescending(x => x.Name, StringComparer.Ordinal);

            foreach (var child in children)
            {
                var childRelative = relative.Length == 0 ? child.Name : $"{relative}/{child.Name}";
                yield return new Entry(childRelative, child, child is DirectoryInfo, child.LinkTarget != null);
            }
        }
    }

    private static Entry? ToEntry(string full, string relative)
    {
        FileSystemInfo info = new FileInfo(full);

        if (info.LinkTarget != null)
            return new Entry(relative, info, false, true);

        if (Directory.Exists(full))
            return new Entry(relative, new DirectoryInfo(full), true, false);

        return info.Exists ? new Entry(relative, info, false, false) : null;
    }

    /// <summary>Writes the entries as gzip compressed tar (pax format), returns the number of entries</summary>
    public static async Task<int> WriteTarGzAsync(Stream output, IEnumerable<Entry> entries, CompressionLevel level, CancellationToken cancellationToken)
    {
        var count = 0;

        await using var gzip = new GZipStream(output, level, leaveOpen: true);
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await WriteTarEntryAsync(tar, entry, cancellationToken))
                    count++;
            }
        }

        return count;
    }

    private static async Task<bool> WriteTarEntryAsync(TarWriter tar, Entry entry, CancellationToken cancellationToken)
    {
        var mode = FileModes.Get(entry.Info) & (UnixFileMode)0x1FF;
        var modified = entry.Info.LastWriteTimeUtc;

        if (entry.IsLink)
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, entry.RelativePath)
            {
                LinkName = entry.Info.LinkTarget!,
                Mode = (UnixFileMode)0x1FF,
                ModificationTime = modified
            }, cancellationToken);
            return true;
        }

        if (entry.IsDirectory)
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, entry.RelativePath + "/")
            {
                Mode = mode,
                ModificationTime = modified
            }, cancellationToken);
            return true;
        }

        // empty files are never opened: fifos and devices have size 0 too and would block
        if (((FileInfo)entry.Info).Length == 0)
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.RelativePath) { Mode = mode, ModificationTime = modified }, cancellationToken);
            return true;
        }

        FileStream file;

        try
        {
            file = new FileStream(entry.Info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // deleted since it was listed
            return false;
        }

        await using var data = new FixedLengthReadStream(file, file.Length);

        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.RelativePath)
        {
            Mode = mode,
            ModificationTime = modified,
            DataStream = data
        }, cancellationToken);
        return true;
    }

    /// <summary>Writes the entries as zip, links are skipped. Returns the number of entries.</summary>
    public static async Task<int> WriteZipAsync(Stream output, IEnumerable<Entry> entries, CancellationToken cancellationToken)
    {
        var count = 0;

        await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsLink)
                continue;

            var mode = (int)(FileModes.Get(entry.Info) & (UnixFileMode)0x1FF);

            if (entry.IsDirectory)
            {
                var directory = zip.CreateEntry(entry.RelativePath + "/");
                directory.ExternalAttributes = (0x4000 | mode) << 16;
                directory.LastWriteTime = ZipTime(entry.Info.LastWriteTime);
                count++;
                continue;
            }

            if (((FileInfo)entry.Info).Length == 0)
            {
                var empty = zip.CreateEntry(entry.RelativePath);
                empty.ExternalAttributes = (0x8000 | mode) << 16;
                empty.LastWriteTime = ZipTime(entry.Info.LastWriteTime);
                count++;
                continue;
            }

            FileStream file;

            try
            {
                file = new FileStream(entry.Info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            await using (file)
            {
                var zipEntry = zip.CreateEntry(entry.RelativePath, CompressionLevel.Optimal);
                zipEntry.ExternalAttributes = (0x8000 | mode) << 16;
                zipEntry.LastWriteTime = ZipTime(entry.Info.LastWriteTime);

                await using var target = await zipEntry.OpenAsync(cancellationToken);
                await file.CopyToAsync(target, cancellationToken);
            }

            count++;
        }

        return count;
    }

    /// <summary>Zip can only store times between 1980 and 2107</summary>
    private static DateTimeOffset ZipTime(DateTime time) =>
        time.Year is >= 1980 and <= 2107 ? time : new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
}
