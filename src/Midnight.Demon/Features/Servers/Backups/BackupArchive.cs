using System.IO.Compression;
using System.Security.Cryptography;
using Midnight.Demon.Features.Servers.Files;

namespace Midnight.Demon.Features.Servers.Backups;

public sealed record BackupArchiveResult(long Bytes, string Checksum, int Entries);

/// <summary>
/// Creates and restores the tar.gz archive of a server volume. Independent of docker and of the server registry, so
/// it works while the docker engine is down (and in tests).
/// </summary>
public static class BackupArchive
{
    /// <summary>
    /// Packs the volume into <paramref name="destination"/> (written as ".part" and renamed at the end), skipping what
    /// <paramref name="ignoredFiles"/> (gitignore syntax) excludes. Returns size and "sha256:hex" of the archive.
    /// </summary>
    public static async Task<BackupArchiveResult> CreateAsync(string volume, string destination, string? ignoredFiles, CancellationToken cancellationToken)
    {
        var matcher = IgnoreMatcher.Parse(ignoredFiles);
        var partial = destination + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes;
            int entries;

            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var counting = new CountingWriteStream(output, hash: hash, leaveOpen: true))
            {
                var walk = Directory.Exists(volume)
                    ? ArchiveWriter.Walk(volume, [""], (path, isDirectory) => IsTemporary(path) || matcher.Matches(path, isDirectory))
                    : [];

                entries = await ArchiveWriter.WriteTarGzAsync(counting, walk, CompressionLevel.Fastest, cancellationToken);
                await counting.FlushAsync(cancellationToken);
                bytes = counting.BytesWritten;
            }

            File.Move(partial, destination, overwrite: true);
            return new BackupArchiveResult(bytes, "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()), entries);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Extracts a backup into the volume (zip slip safe, see <see cref="ArchiveExtractor"/>). With
    /// <paramref name="truncate"/> everything in the volume is deleted first.
    /// </summary>
    public static async Task<ExtractResult> RestoreAsync(string archive, string volume, bool truncate, Action<string>? created, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(volume);

        if (truncate)
            Truncate(volume);

        return await ArchiveExtractor.ExtractAsync(archive, ArchiveFormat.TarGz, volume, new ExtractOptions { Created = created }, cancellationToken);
    }

    /// <summary>Deletes everything inside of the directory, links are removed and never followed</summary>
    public static void Truncate(string directory)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 };

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).ToList())
            ServerFiles.DeleteEntry(entry.FullName);
    }

    /// <summary>Temporary files of running uploads and downloads</summary>
    private static bool IsTemporary(string path) =>
        path.EndsWith(".midnight-upload", StringComparison.Ordinal) || path.EndsWith(".midnight-download", StringComparison.Ordinal);

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
}
