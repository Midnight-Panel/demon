using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Midnight.Demon.Core.Configuration;
using Midnight.Shared.Http.Servers;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>
/// The file manager of a server volume. Every path is relative to the volume ("/" = the volume root) and resolved
/// with <see cref="ServerFileSystem.ResolveInside"/> (no "..", no symlink leaving the volume). Files matching the
/// shard's FileDenylist can't be read, written, renamed or deleted (403). New files belong to the container user.
/// Works without the docker engine.
/// </summary>
public sealed class ServerFiles
{
    private static readonly ConditionalWeakTable<List<string>, IgnoreMatcher> DenylistCache = new();

    private readonly ServerFileSystem _files;
    private readonly FilesConfiguration _options;
    private readonly ILogger<ServerFiles> _logger;
    private readonly CancellationToken _stopping;

    public ServerFiles(ServerFileSystem files, DemonConfiguration configuration, ILogger<ServerFiles> logger, IHostApplicationLifetime lifetime)
    {
        _files = files;
        _options = configuration.Files;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
    }

    // ─── Paths ───────────────────────────────────────────────────────────────

    public string GetVolume(ServerInstance server) => _files.EnsureVolume(server.Uuid);

    /// <summary>Full path of a path of the server, 403 when it leaves the volume</summary>
    public static string Resolve(string volume, string path, bool allowRoot = false)
    {
        try
        {
            return ServerFileSystem.ResolveInside(volume, path, allowRoot);
        }
        catch (UnauthorizedAccessException e)
        {
            throw ServerApiException.Forbidden("Access denied", e.Message);
        }
    }

    /// <summary>"root" + "name" as one path of the server ("../x" is resolved later and can't leave the volume)</summary>
    public static string Join(string root, string path) => $"{root.TrimEnd('/', '\\')}/{path.TrimStart('/', '\\')}";

    /// <summary>The denylist of the server (parsed once per configuration)</summary>
    public static IgnoreMatcher GetDenylist(ServerInstance server)
    {
        var list = server.Configuration.Process.Features.FileDenylist;
        return list.Count == 0 ? IgnoreMatcher.Empty : DenylistCache.GetValue(list, IgnoreMatcher.Parse);
    }

    /// <summary>403 when the path (or the real path behind a link) matches the denylist</summary>
    public static void CheckDenylist(IgnoreMatcher denylist, string volume, string fullPath, bool recursive = false)
    {
        if (denylist.IsEmpty)
            return;

        var isDirectory = Directory.Exists(fullPath);

        if (IsDenied(denylist, volume, fullPath, isDirectory))
            throw Denied(ServerFileSystem.ToRelative(volume, fullPath));

        if (!recursive || !isDirectory || new FileInfo(fullPath).LinkTarget != null)
            return;

        foreach (var entry in ArchiveWriter.Walk(volume, [ServerFileSystem.ToRelative(volume, fullPath)]))
        {
            if (denylist.IsExcluded(entry.RelativePath, entry.IsDirectory))
                throw Denied(entry.RelativePath);
        }
    }

    private static bool IsDenied(IgnoreMatcher denylist, string volume, string fullPath, bool isDirectory)
    {
        if (denylist.IsExcluded(ServerFileSystem.ToRelative(volume, fullPath), isDirectory))
            return true;

        // a link inside of the volume must not open a way around the denylist
        var realVolume = ServerFileSystem.GetRealPath(volume);
        var real = ServerFileSystem.GetRealPath(fullPath);

        return ServerFileSystem.IsInside(realVolume, real) && real != realVolume &&
               denylist.IsExcluded(ServerFileSystem.ToRelative(realVolume, real), isDirectory);
    }

    private static ServerApiException Denied(string relative) =>
        ServerApiException.Forbidden("This file is protected", $"'/{relative}' is on the file denylist of the server");

    /// <summary>True when something (also a dangling link) exists at the path</summary>
    public static bool EntryExists(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget != null;

    // ─── Listing ─────────────────────────────────────────────────────────────

    public List<FileEntryResponse> List(ServerInstance server, string directory)
    {
        var volume = GetVolume(server);
        var full = Resolve(volume, directory, allowRoot: true);

        if (File.Exists(full))
            throw ServerApiException.BadRequest("Not a directory", $"'{directory}' is a file");

        if (!Directory.Exists(full))
            throw ServerApiException.FileNotFound(directory);

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };

        return new DirectoryInfo(full).EnumerateFileSystemInfos("*", options)
            .Select(info => ToEntry(volume, info))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Describes a file, a link is described by its target when that is inside of the volume</summary>
    public static FileEntryResponse ToEntry(string volume, FileSystemInfo info)
    {
        var isLink = info.LinkTarget != null;
        FileSystemInfo target = info;

        if (isLink)
        {
            try
            {
                var resolved = ServerFileSystem.ResolveInside(volume, ServerFileSystem.ToRelative(volume, info.FullName));
                target = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
                target = target.ResolveLinkTarget(returnFinalTarget: true) ?? target;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // points outside of the volume: only the link itself is shown
            }
        }

        var isDirectory = target is DirectoryInfo { Exists: true };
        var size = target is FileInfo { Exists: true } file ? file.Length : 0;
        var mode = FileModes.Get(info);
        var (mime, editable) = isDirectory ? (FileTypes.DirectoryMime, false)
            : target.Exists && target != info || !isLink ? FileTypes.Detect(target.FullName, size)
            : (FileTypes.BinaryMime, false);

        return new FileEntryResponse
        {
            Name = info.Name,
            Size = size,
            IsDirectory = isDirectory,
            IsSymlink = isLink,
            Mode = FileModes.Format(mode, FileModes.TypeOf(info)),
            ModeBits = FileModes.ToOctal(mode),
            MimeType = mime,
            IsEditable = editable,
            ModifiedAt = new DateTimeOffset((target.Exists ? target : info).LastWriteTimeUtc, TimeSpan.Zero)
        };
    }

    // ─── Contents ────────────────────────────────────────────────────────────

    /// <summary>The bytes of a text file: 413 when larger than 5 MiB, 400 for binary files</summary>
    public async Task<byte[]> ReadContentsAsync(ServerInstance server, string file, CancellationToken cancellationToken)
    {
        var volume = GetVolume(server);
        var full = ResolveExistingFile(volume, file);
        CheckDenylist(GetDenylist(server), volume, full);

        var length = new FileInfo(full).Length;

        if (length > FileTypes.MaxEditableBytes)
            throw ServerApiException.TooLarge("File too large", $"Only files up to {FileTypes.MaxEditableBytes / 1024 / 1024} MiB can be opened in the editor, download it instead");

        // empty, or a fifo / device (size 0) that would block on open
        if (length == 0)
            return [];

        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);

        if (bytes.Length > FileTypes.MaxEditableBytes)
            throw ServerApiException.TooLarge("File too large", "The file grew while it was read");

        if (!FileTypes.LooksLikeText(bytes, complete: true))
            throw ServerApiException.BadRequest("Binary file", "The file is not a UTF-8 text file and can't be opened in the editor");

        return bytes;
    }

    /// <summary>Full path, download name, mime type and size of a file</summary>
    public (string Path, string Name, string Mime, long Length) GetDownload(ServerInstance server, string file)
    {
        var volume = GetVolume(server);
        var full = ResolveExistingFile(volume, file);
        CheckDenylist(GetDenylist(server), volume, full);

        var name = Path.GetFileName(full);
        return (full, name, FileTypes.GetMimeByName(name) ?? FileTypes.BinaryMime, new FileInfo(full).Length);
    }

    private static string ResolveExistingFile(string volume, string file)
    {
        var full = Resolve(volume, file);

        if (Directory.Exists(full))
            throw ServerApiException.BadRequest("Not a file", $"'{file}' is a directory");

        if (!File.Exists(full))
            throw ServerApiException.FileNotFound(file);

        return full;
    }

    // ─── Writing ─────────────────────────────────────────────────────────────

    /// <summary>Replaces (or creates) a file with the request body, created atomically via a temporary file</summary>
    public async Task WriteAsync(ServerInstance server, string file, HttpRequest request, CancellationToken cancellationToken)
    {
        var volume = GetVolume(server);
        var full = Resolve(volume, file);
        CheckDenylist(GetDenylist(server), volume, full);

        if (Directory.Exists(full))
            throw ServerApiException.BadRequest("Not a file", $"'{file}' is a directory");

        AllowLargeBody(request);

        if (request.ContentLength > _options.MaxUploadBytes)
            throw ServerApiException.TooLarge("File too large", $"At most {_options.MaxUploadBytes} bytes can be written at once");

        await WriteFileAsync(server, volume, full, request.Body, cancellationToken);
    }

    /// <summary>Streams one upload or write into a file, checking the disk limit while writing</summary>
    private async Task<long> WriteFileAsync(ServerInstance server, string volume, string full, Stream data, CancellationToken cancellationToken)
    {
        var existing = File.Exists(full) ? new FileInfo(full) : null;
        var existingSize = existing?.Length ?? 0;
        var maxBytes = SaturatingAdd(server.GetRemainingDiskBytes(), existingSize);

        var directory = Path.GetDirectoryName(full)!;
        CreateDirectories(volume, directory);

        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.midnight-upload");
        long written;

        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var counting = new CountingWriteStream(output, maxBytes, leaveOpen: true))
            {
                try
                {
                    await data.CopyToAsync(counting, cancellationToken);
                }
                catch (SizeLimitExceededException)
                {
                    throw ServerApiException.TooLarge("Disk limit exceeded", "The server does not have enough disk space left for this file");
                }

                written = counting.BytesWritten;
            }

            // an edited file keeps its permissions (e.g. an executable start script)
            if (existing != null && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, existing.UnixFileMode);

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }

        SetOwner(full);
        server.AddDiskBytes(written - existingSize);
        return written;
    }

    /// <summary>multipart/form-data with one or more "files" parts, streamed to disk one by one</summary>
    public async Task<int> UploadAsync(ServerInstance server, string directory, HttpRequest request, CancellationToken cancellationToken)
    {
        var volume = GetVolume(server);
        var target = Resolve(volume, directory, allowRoot: true);
        var denylist = GetDenylist(server);

        if (File.Exists(target))
            throw ServerApiException.BadRequest("Not a directory", $"'{directory}' is a file");

        var boundary = request.ContentType is { } contentType && MediaTypeHeaderValue.TryParse(contentType, out var media) &&
                       media.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            ? HeaderUtilities.RemoveQuotes(media.Boundary).Value
            : null;

        if (string.IsNullOrEmpty(boundary))
            throw ServerApiException.BadRequest("Invalid upload", "Expected multipart/form-data with \"files\" parts");

        AllowLargeBody(request);

        if (request.ContentLength > _options.MaxUploadBytes)
            throw ServerApiException.TooLarge("Upload too large", $"At most {_options.MaxUploadBytes} bytes can be uploaded at once");

        var reader = new MultipartReader(boundary, request.Body);
        var count = 0;

        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data") ||
                !HeaderUtilities.RemoveQuotes(disposition.Name).Equals("files", StringComparison.Ordinal))
            {
                await section.Body.DrainAsync(cancellationToken);
                continue;
            }

            var rawName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            var name = FileNames.Sanitize(rawName)
                       ?? throw ServerApiException.BadRequest("Invalid upload", "Every \"files\" part needs a file name");

            var full = Resolve(volume, Join(ServerFileSystem.ToRelative(volume, target), name));
            CheckDenylist(denylist, volume, full);

            if (Directory.Exists(full))
                throw ServerApiException.Conflict($"'{name}' already exists as a directory");

            await WriteFileAsync(server, volume, full, section.Body, cancellationToken);
            count++;
        }

        if (count == 0)
            throw ServerApiException.BadRequest("Invalid upload", "The request contains no \"files\" part");

        return count;
    }

    /// <summary>Kestrel allows 30 MB by default, uploads are limited by MaxUploadBytes instead</summary>
    private void AllowLargeBody(HttpRequest request)
    {
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = _options.MaxUploadBytes;
    }

    // ─── Folders, rename, copy, delete, chmod ────────────────────────────────

    public void CreateFolder(ServerInstance server, CreateFolderRequest request)
    {
        var volume = GetVolume(server);
        var full = Resolve(volume, Join(request.Root, request.Name));
        CheckDenylist(GetDenylist(server), volume, full);

        if (EntryExists(full))
            throw ServerApiException.Conflict($"'{request.Name}' already exists");

        CreateDirectories(volume, full);
    }

    public void Rename(ServerInstance server, RenameFilesRequest request)
    {
        var volume = GetVolume(server);
        var denylist = GetDenylist(server);
        var moves = new List<(string From, string To)>();

        foreach (var file in request.Files)
        {
            var from = Resolve(volume, Join(request.Root, file.From));
            var to = Resolve(volume, Join(request.Root, file.To));

            if (!EntryExists(from))
                throw ServerApiException.FileNotFound(file.From);

            CheckDenylist(denylist, volume, from, recursive: true);
            CheckDenylist(denylist, volume, to);

            if (string.Equals(from, to, StringComparison.Ordinal))
                continue;

            if (EntryExists(to))
                throw ServerApiException.Conflict($"'{file.To}' already exists");

            if (Directory.Exists(from) && new FileInfo(from).LinkTarget == null && ServerFileSystem.IsInside(from, to))
                throw ServerApiException.BadRequest("Invalid move", "A directory can't be moved into itself");

            moves.Add((from, to));
        }

        foreach (var (from, to) in moves)
        {
            CreateDirectories(volume, Path.GetDirectoryName(to)!);
            Move(from, to);
        }
    }

    private static void Move(string from, string to)
    {
        // links are moved themselves, never their targets
        if (new FileInfo(from).LinkTarget == null && Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    /// <summary>Copies a file next to itself as "name copy.ext" (or "name copy 2.ext", ...)</summary>
    public void Copy(ServerInstance server, CopyFileRequest request)
    {
        var volume = GetVolume(server);
        var source = Resolve(volume, request.Location);

        if (Directory.Exists(source))
            throw ServerApiException.BadRequest("Not a file", "Only files can be copied");

        if (!File.Exists(source))
            throw ServerApiException.FileNotFound(request.Location);

        var denylist = GetDenylist(server);
        CheckDenylist(denylist, volume, source);

        var size = new FileInfo(source).Length;

        if (size > server.GetRemainingDiskBytes())
            throw ServerApiException.TooLarge("Disk limit exceeded", "The server does not have enough disk space left for the copy");

        var directory = Path.GetDirectoryName(source)!;
        var name = FileNames.GetCopyName(Path.GetFileName(source), candidate => EntryExists(Path.Combine(directory, candidate)));
        var target = Path.Combine(directory, name);
        CheckDenylist(denylist, volume, target);

        if (size == 0)
            File.Create(target).Dispose(); // a fifo or device (size 0) would block File.Copy
        else
            File.Copy(source, target, overwrite: false);
        SetOwner(target);
        server.AddDiskBytes(size);
    }

    public void Delete(ServerInstance server, DeleteFilesRequest request)
    {
        var volume = GetVolume(server);
        var denylist = GetDenylist(server);
        var paths = new List<string>();

        // everything is checked before anything is deleted
        foreach (var file in request.Files)
        {
            var full = Resolve(volume, Join(request.Root, file));

            if (!EntryExists(full))
                throw ServerApiException.FileNotFound(file);

            CheckDenylist(denylist, volume, full, recursive: true);
            paths.Add(full);
        }

        foreach (var path in paths)
            DeleteEntry(path);

        MeasureInBackground(server);
    }

    /// <summary>Deletes a file, a link (never its target) or a directory with everything inside</summary>
    internal static void DeleteEntry(string path)
    {
        var info = new FileInfo(path);

        if (info.LinkTarget != null)
            info.Delete();
        else if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (info.Exists)
            info.Delete();
    }

    public void Chmod(ServerInstance server, ChmodFilesRequest request)
    {
        if (OperatingSystem.IsWindows())
            throw ServerApiException.BadRequest("Not supported", "File modes are not supported on Windows");

        var volume = GetVolume(server);
        var denylist = GetDenylist(server);
        var changes = new List<(string Path, UnixFileMode Mode)>();

        foreach (var file in request.Files)
        {
            if (!FileModes.TryParse(file.Mode, out var mode))
                throw ServerApiException.BadRequest("Invalid mode", $"'{file.Mode}' is not an octal mode like 644 or 755");

            var full = Resolve(volume, Join(request.Root, file.File));

            if (!Path.Exists(full))
                throw ServerApiException.FileNotFound(file.File);

            CheckDenylist(denylist, volume, full);

            // setuid / setgid make no sense for server files and could be abused, the sticky bit is harmless
            changes.Add((full, mode & ~(UnixFileMode.SetUser | UnixFileMode.SetGroup)));
        }

        foreach (var (path, mode) in changes)
            File.SetUnixFileMode(path, mode);
    }

    // ─── Archives ────────────────────────────────────────────────────────────

    /// <summary>Packs the files into "archive-{timestamp}.tar.gz" (or .zip) in the root directory</summary>
    public async Task<FileEntryResponse> CompressAsync(ServerInstance server, CompressFilesRequest request)
    {
        var volume = GetVolume(server);
        var root = Resolve(volume, request.Root, allowRoot: true);
        var denylist = GetDenylist(server);

        if (!Directory.Exists(root))
            throw ServerApiException.FileNotFound(request.Root);

        var relative = new List<string>();

        foreach (var file in request.Files)
        {
            var full = Resolve(volume, Join(request.Root, file));

            if (!EntryExists(full))
                throw ServerApiException.FileNotFound(file);

            if (!ServerFileSystem.IsInside(root, full) || full.Length <= root.TrimEnd(Path.DirectorySeparatorChar).Length)
                throw ServerApiException.BadRequest("Invalid file", $"'{file}' is not inside of '{request.Root}'");

            CheckDenylist(denylist, volume, full);
            relative.Add(Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/'));
        }

        var zip = request.Format == "zip";
        var name = FileNames.GetArchiveName(DateTimeOffset.UtcNow, zip ? ".zip" : ".tar.gz", candidate => EntryExists(Path.Combine(root, candidate)));
        var archive = Path.Combine(root, name);
        var rootRelative = ServerFileSystem.ToRelative(volume, root);

        // the archive itself and denied files are never packed
        bool Exclude(string path, bool isDirectory)
        {
            var volumeRelative = rootRelative.Length == 0 ? path : $"{rootRelative}/{path}";
            return string.Equals(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), archive, StringComparison.Ordinal) ||
                   denylist.IsExcluded(volumeRelative, isDirectory);
        }

        using var timeout = CreateTimeout();
        long size;

        try
        {
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using var counting = new CountingWriteStream(output, server.GetRemainingDiskBytes(), leaveOpen: true);

            await Task.Run(async () =>
            {
                var entries = ArchiveWriter.Walk(root, relative, Exclude);

                if (zip)
                    await ArchiveWriter.WriteZipAsync(counting, entries, timeout.Token);
                else
                    await ArchiveWriter.WriteTarGzAsync(counting, entries, CompressionLevel.Optimal, timeout.Token);
            }, timeout.Token);

            await counting.FlushAsync(timeout.Token);
            size = counting.BytesWritten;
        }
        catch (Exception e)
        {
            TryDeleteFile(archive);
            throw TranslateArchiveError(e, timeout);
        }

        SetOwner(archive);
        server.AddDiskBytes(size);

        return ToEntry(volume, new FileInfo(archive));
    }

    /// <summary>Extracts an archive into the root directory (zip slip safe, limited by the free disk space)</summary>
    public async Task DecompressAsync(ServerInstance server, DecompressFileRequest request)
    {
        var volume = GetVolume(server);
        var root = Resolve(volume, request.Root, allowRoot: true);
        var denylist = GetDenylist(server);

        if (!Directory.Exists(root))
            throw ServerApiException.FileNotFound(request.Root);

        var archive = ResolveExistingFile(volume, Join(request.Root, request.File));
        CheckDenylist(denylist, volume, archive);

        var format = ArchiveExtractor.DetectFormat(archive)
                     ?? throw ServerApiException.BadRequest("Unsupported archive", "Supported formats: zip, tar, tar.gz / tgz and gz");

        var rootRelative = ServerFileSystem.ToRelative(volume, root);
        var options = new ExtractOptions
        {
            MaxBytes = server.GetRemainingDiskBytes(),
            Skip = denylist.IsEmpty ? null : (path, isDirectory) => denylist.IsExcluded(rootRelative.Length == 0 ? path : $"{rootRelative}/{path}", isDirectory),
            Created = SetOwner
        };

        using var timeout = CreateTimeout();
        ExtractResult result;

        try
        {
            result = await Task.Run(() => ArchiveExtractor.ExtractAsync(archive, format, root, options, timeout.Token), timeout.Token);
        }
        catch (Exception e)
        {
            MeasureInBackground(server);
            throw TranslateArchiveError(e, timeout);
        }

        server.AddDiskBytes(result.Bytes);
        MeasureInBackground(server);

        if (result.Unsafe > 0)
        {
            _logger.LogWarning("Skipped {Count} unsafe entries of the archive {Archive} of server {Uuid}", result.Unsafe, request.File, server.Uuid);
            server.Notice($"{result.Unsafe} entries of {Path.GetFileName(archive)} were not extracted (paths outside of the directory, links or special files)");
        }
    }

    /// <summary>A closed browser tab should not leave half an archive behind: only the timeout and a shutdown abort</summary>
    private CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ArchiveTimeoutSeconds));
        return timeout;
    }

    private Exception TranslateArchiveError(Exception e, CancellationTokenSource timeout) => e switch
    {
        ServerApiException => e,
        SizeLimitExceededException => ServerApiException.TooLarge("Disk limit exceeded", e.Message),
        OperationCanceledException when timeout.IsCancellationRequested && !_stopping.IsCancellationRequested =>
            new ServerApiException(StatusCodes.Status408RequestTimeout, "Timeout", $"The archive operation did not finish within {_options.ArchiveTimeoutSeconds} seconds"),
        InvalidDataException or EndOfStreamException or FormatException =>
            ServerApiException.BadRequest("Invalid archive", "The archive is damaged or not in a supported format"),
        _ => e
    };

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Creates the directory and missing parents inside of the volume, new ones belong to the container user</summary>
    public void CreateDirectories(string volume, string directory)
    {
        var missing = new Stack<string>();

        for (var current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
        {
            if (!ServerFileSystem.IsInside(volume, current))
                break;

            if (File.Exists(current) || new FileInfo(current).LinkTarget != null)
                throw ServerApiException.Conflict($"'/{ServerFileSystem.ToRelative(volume, current)}' is a file");

            missing.Push(current);
        }

        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            SetOwner(path);
        }
    }

    /// <summary>Gives a created file to the container user (Linux only, failures are logged)</summary>
    public void SetOwner(string path)
    {
        try
        {
            _files.FixPermissions(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not change the owner of {Path}: {Error}", path, e.Message);
        }
    }

    internal void MeasureInBackground(ServerInstance server)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await server.MeasureDiskAsync(_stopping);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug("Could not measure the disk of server {Uuid}: {Error}", server.Uuid, e.Message);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }, CancellationToken.None);
    }

    private static void TryDeleteFile(string path)
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

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
