using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Features.Servers;
using Midnight.Demon.Features.Servers.Backups;

namespace Midnight.Demon.Tests;

public class BackupArchiveTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _volume;
    private readonly string _backup;

    public BackupArchiveTests()
    {
        _volume = Directory.CreateDirectory(_temp.Combine("volume")).FullName;
        _backup = _temp.Combine("backups", "server", "backup.tar.gz");
    }

    public void Dispose() => _temp.Dispose();

    private void Write(string relative, string content)
    {
        var full = Path.Combine([_volume, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<string> EntryNames()
    {
        using var file = File.OpenRead(_backup);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new List<string>();

        while (tar.GetNextEntry() is { } entry)
            names.Add(entry.Name.TrimEnd('/'));

        return names;
    }

    [Fact]
    public async Task CreateAndRestore_Roundtrip()
    {
        Write("server.properties", "motd=hello");
        Write("world/level.dat", "level");
        Write("world/region/r.0.0.mca", new string('r', 100_000));
        Write("logs/latest.log", "log");
        Write("cache/tmp.bin", "cache");
        Write("plugins/a.jar", "jar");
        Write("plugins/keep.jar", "keep");
        Directory.CreateDirectory(Path.Combine(_volume, "empty"));
        File.CreateSymbolicLink(Path.Combine(_volume, "level-link"), "world/level.dat");

        var result = await BackupArchive.CreateAsync(_volume, _backup, "logs/\n/cache\n*.jar\n!keep.jar", CancellationToken.None);

        // the checksum is the sha256 of the archive file
        await using (var stream = File.OpenRead(_backup))
            Assert.Equal("sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)), result.Checksum);

        Assert.Equal(new FileInfo(_backup).Length, result.Bytes);
        Assert.False(File.Exists(_backup + ".part"));

        var names = EntryNames();
        Assert.Contains("world/region/r.0.0.mca", names);
        Assert.Contains("plugins/keep.jar", names);
        Assert.Contains("empty", names);
        Assert.DoesNotContain(names, name => name.StartsWith("logs") || name.StartsWith("cache"));
        Assert.DoesNotContain("plugins/a.jar", names);

        // the server changes after the backup
        Write("server.properties", "motd=changed");
        Write("new-file.txt", "new");
        File.Delete(Path.Combine(_volume, "world", "level.dat"));

        var restore = await BackupArchive.RestoreAsync(_backup, _volume, truncate: true, created: null, CancellationToken.None);

        Assert.Equal(0, restore.Unsafe);
        Assert.Equal("motd=hello", File.ReadAllText(Path.Combine(_volume, "server.properties")));
        Assert.Equal("level", File.ReadAllText(Path.Combine(_volume, "world", "level.dat")));
        Assert.Equal(100_000, new FileInfo(Path.Combine(_volume, "world", "region", "r.0.0.mca")).Length);
        Assert.Equal("world/level.dat", new FileInfo(Path.Combine(_volume, "level-link")).LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(_volume, "empty")));
        // truncate removed files that were not in the backup, ignored files are gone too
        Assert.False(File.Exists(Path.Combine(_volume, "new-file.txt")));
        Assert.False(Directory.Exists(Path.Combine(_volume, "logs")));
    }

    [Fact]
    public async Task RestoreWithoutTruncate_KeepsOtherFiles()
    {
        Write("a.txt", "old");
        await BackupArchive.CreateAsync(_volume, _backup, "", CancellationToken.None);

        Write("a.txt", "new");
        Write("b.txt", "b");

        await BackupArchive.RestoreAsync(_backup, _volume, truncate: false, created: null, CancellationToken.None);

        Assert.Equal("old", File.ReadAllText(Path.Combine(_volume, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(_volume, "b.txt")));
    }

    [Fact]
    public async Task Truncate_NeverFollowsLinks()
    {
        var outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(_volume, "out"), outside);
        Write("a.txt", "a");
        await BackupArchive.CreateAsync(_volume, _backup, "out", CancellationToken.None);

        await BackupArchive.RestoreAsync(_backup, _volume, truncate: true, created: null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.False(Path.Exists(Path.Combine(_volume, "out")));
    }

    [Fact]
    public async Task GrowingFiles_DoNotCorruptTheArchive()
    {
        Write("latest.log", new string('a', 50_000));

        // the stream writes exactly the length known when the header was written
        await using var file = File.OpenRead(Path.Combine(_volume, "latest.log"));
        await using var fixedLength = new Features.Servers.Files.FixedLengthReadStream(file, 40_000);
        var copy = new MemoryStream();
        await fixedLength.CopyToAsync(copy);
        Assert.Equal(40_000, copy.Length);

        await using var shorter = new Features.Servers.Files.FixedLengthReadStream(new MemoryStream(new byte[10]), 100);
        copy = new MemoryStream();
        await shorter.CopyToAsync(copy);
        Assert.Equal(100, copy.Length);
    }

    [Fact]
    public void BackupPaths_AreBelowTheDataPath()
    {
        var files = new ServerFileSystem(new DemonConfiguration { DataPath = _temp.Path, Servers = { ContainerUser = "" } });
        var server = Guid.NewGuid();
        var backup = Guid.NewGuid();

        Assert.Equal(Path.Combine(_temp.Path, "backups", server.ToString(), $"{backup}.tar.gz"), files.GetBackupPath(server, backup));

        Directory.CreateDirectory(files.GetBackupDirectory(server));
        File.WriteAllText(files.GetBackupPath(server, backup), "x");
        files.DeleteBackups(server);

        Assert.False(Directory.Exists(files.GetBackupDirectory(server)));
    }
}
