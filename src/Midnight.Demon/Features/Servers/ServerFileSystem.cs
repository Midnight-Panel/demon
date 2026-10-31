using System.Runtime.InteropServices;
using Midnight.Demon.Core.Configuration;

namespace Midnight.Demon.Features.Servers;

/// <summary>Paths of the server volumes and install logs below the data path, with path traversal protection</summary>
public class ServerFileSystem
{
    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly uint? _uid;
    private readonly uint? _gid;

    public ServerFileSystem(DemonConfiguration configuration)
    {
        var dataPath = Path.GetFullPath(configuration.DataPath);

        VolumesRoot = Path.Combine(dataPath, "volumes");
        InstallLogsRoot = Path.Combine(dataPath, "logs", "install");
        InstallScriptsRoot = Path.Combine(dataPath, "install");
        BackupsRoot = Path.Combine(dataPath, "backups");

        if (ServerRuntimeConfiguration.TryParseUser(configuration.Servers.ContainerUser, out var uid, out var gid))
        {
            _uid = uid;
            _gid = gid;
        }
    }

    public string VolumesRoot { get; }
    public string InstallLogsRoot { get; }
    public string InstallScriptsRoot { get; }
    public string BackupsRoot { get; }

    public string GetVolumePath(Guid uuid) => Path.Combine(VolumesRoot, uuid.ToString());
    public string GetInstallLogPath(Guid uuid) => Path.Combine(InstallLogsRoot, $"{uuid}.log");
    public string GetInstallScriptDirectory(Guid uuid) => Path.Combine(InstallScriptsRoot, uuid.ToString());

    public string GetBackupDirectory(Guid uuid) => Path.Combine(BackupsRoot, uuid.ToString());
    public string GetBackupPath(Guid uuid, Guid backupUuid) => Path.Combine(GetBackupDirectory(uuid), $"{backupUuid}.tar.gz");

    /// <summary>True when files created by the demon are chowned to the container user</summary>
    public bool ChangesOwnership => _uid != null && OperatingSystem.IsLinux();

    public string EnsureVolume(Guid uuid)
    {
        var path = GetVolumePath(uuid);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes the volume of a server. Symlinks inside are removed, never followed.</summary>
    public void DeleteVolume(Guid uuid)
    {
        var path = Path.GetFullPath(GetVolumePath(uuid));

        if (!IsInside(VolumesRoot, path) || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), VolumesRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Refusing to delete '{path}', it is not a volume below '{VolumesRoot}'");

        var info = new DirectoryInfo(path);

        if (info.LinkTarget != null)
        {
            // the volume itself is a link: remove the link only
            info.Delete();
            return;
        }

        if (info.Exists)
            info.Delete(recursive: true);
    }

    /// <summary>Deletes every backup archive of a server</summary>
    public void DeleteBackups(Guid uuid)
    {
        var directory = GetBackupDirectory(uuid);

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    public void DeleteInstallFiles(Guid uuid)
    {
        var log = GetInstallLogPath(uuid);

        if (File.Exists(log))
            File.Delete(log);

        var scripts = GetInstallScriptDirectory(uuid);

        if (Directory.Exists(scripts))
            Directory.Delete(scripts, recursive: true);
    }

    /// <summary>
    /// Resolves a path of a server (relative to its volume, a leading "/" means the volume root) to an absolute path.
    /// Throws when the path or a symlink on the way leaves the volume. The volume root itself is refused unless
    /// <paramref name="allowRoot"/> is set.
    /// </summary>
    public static string ResolveInside(string root, string relativePath, bool allowRoot = false)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var trimmed = relativePath.Replace('\\', '/').Trim('/');

        if (relativePath.Contains('\0') || (trimmed.Length == 0 && !allowRoot))
            throw new UnauthorizedAccessException($"Invalid path '{relativePath}'");

        var full = trimmed.Length == 0 ? fullRoot : Path.GetFullPath(Path.Combine(fullRoot, trimmed)).TrimEnd(Path.DirectorySeparatorChar);

        if (!IsInside(fullRoot, full) || (full.Length <= fullRoot.Length && !allowRoot))
            throw new UnauthorizedAccessException($"The path '{relativePath}' leaves the server directory");

        // the real path (every link on the way resolved) must still be inside of the real volume path
        if (!IsInside(GetRealPath(fullRoot), GetRealPath(full)))
            throw new UnauthorizedAccessException($"The path '{relativePath}' points outside of the server directory");

        return full;
    }

    /// <summary>
    /// The path with every symlink resolved, like realpath(3). Parts that don't exist (yet) are appended as they are,
    /// a dangling link is resolved to where a write through it would end up.
    /// </summary>
    public static string GetRealPath(string path, int depth = 0)
    {
        // same limit as the kernel, protects against link loops
        if (depth > 40)
            throw new UnauthorizedAccessException($"Too many levels of symbolic links in '{path}'");

        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        var parts = full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            var linkTarget = new FileInfo(next).LinkTarget;

            if (linkTarget != null)
            {
                // relative targets are relative to the (already real) directory of the link
                current = GetRealPath(Path.GetFullPath(linkTarget, current), depth + 1);
                continue;
            }

            if (!Path.Exists(next))
                return Path.Combine([next, ..parts[(i + 1)..]]);

            current = next;
        }

        return current;
    }

    /// <summary>Path of a volume entry relative to the volume, with "/" separators and without leading slash ("" = the root)</summary>
    public static string ToRelative(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), fullPath);
        return relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or below it</summary>
    public static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    /// <summary>Sum of all file sizes below the directory, symlinks are not followed</summary>
    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        long size = 0;

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", RecursiveOptions))
        {
            try
            {
                size += file.Length;
            }
            catch (IOException)
            {
                // deleted while counting
            }
        }

        return size;
    }

    /// <summary>Gives the configured container user (Servers.ContainerUser) ownership of the path and everything below. Linux only.</summary>
    public void FixPermissions(string path)
    {
        if (_uid == null || _gid == null || !OperatingSystem.IsLinux() || !Path.Exists(path))
            return;

        Chown(path);

        var pending = new Stack<DirectoryInfo>();

        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget == null)
            pending.Push(new DirectoryInfo(path));

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                Chown(entry.FullName);

                // never descend into links, they may point outside of the volume
                if (entry is DirectoryInfo child && entry.LinkTarget == null)
                    pending.Push(child);
            }
        }
    }

    private void Chown(string path)
    {
        // lchown: links themselves are changed, their targets never
        if (NativeMethods.lchown(path, _uid!.Value, _gid!.Value) != 0)
            throw new IOException($"Could not change the owner of '{path}' (errno {Marshal.GetLastPInvokeError()})");
    }

    private static class NativeMethods
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int lchown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
    }
}
