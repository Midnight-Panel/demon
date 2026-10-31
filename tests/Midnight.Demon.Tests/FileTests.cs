using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Features.Docker;
using Midnight.Demon.Features.Servers;
using Midnight.Demon.Features.Servers.Files;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Http.Servers;

namespace Midnight.Demon.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("midnight-files-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class PathResolutionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _volume;
    private readonly string _outside;

    public PathResolutionTests()
    {
        _volume = Directory.CreateDirectory(_temp.Combine("volume")).FullName;
        _outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Root_IsOnlyAllowedWhenRequested()
    {
        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(_volume, "/"));
        Assert.Equal(_volume, ServerFileSystem.ResolveInside(_volume, "/", allowRoot: true));
        Assert.Equal(_volume, ServerFileSystem.ResolveInside(_volume, "a/..", allowRoot: true));
        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(_volume, "..", allowRoot: true));
    }

    [Fact]
    public void LinksInsideOfTheVolume_AreFine()
    {
        Directory.CreateDirectory(Path.Combine(_volume, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_volume, "alias"), "real");

        Assert.Equal(Path.Combine(_volume, "alias", "file.txt"), ServerFileSystem.ResolveInside(_volume, "alias/file.txt"));
    }

    [Fact]
    public void LinkChains_LeavingTheVolume_AreRejected()
    {
        // alias → real (inside), real/escape → ../../outside: only the real path shows the escape
        Directory.CreateDirectory(Path.Combine(_volume, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_volume, "alias"), "real");
        Directory.CreateSymbolicLink(Path.Combine(_volume, "real", "escape"), Path.Combine("..", "..", "outside"));

        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(_volume, "alias/escape/secret.txt"));
    }

    [Fact]
    public void DanglingLinksToOutside_AreRejected()
    {
        File.CreateSymbolicLink(Path.Combine(_volume, "dangling"), Path.Combine(_outside, "does-not-exist"));

        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(_volume, "dangling"));
    }

    [Fact]
    public void LinkLoops_AreRejected()
    {
        File.CreateSymbolicLink(Path.Combine(_volume, "a"), "b");
        File.CreateSymbolicLink(Path.Combine(_volume, "b"), "a");

        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(_volume, "a"));
    }

    [Fact]
    public void ErrorsOfTheFileManager_Are403()
    {
        var error = Assert.Throws<ServerApiException>(() => ServerFiles.Resolve(_volume, "../outside"));
        Assert.Equal(403, error.StatusCode);
    }
}

public class IgnoreMatcherTests
{
    [Theory]
    // plain names match at any level
    [InlineData("logs", "logs", true, true)]
    [InlineData("logs", "a/b/logs", true, true)]
    [InlineData("*.log", "latest.log", false, true)]
    [InlineData("*.log", "logs/latest.log", false, true)]
    [InlineData("*.log", "latest.log.gz", false, false)]
    // leading / anchors to the root
    [InlineData("/cache", "cache", true, true)]
    [InlineData("/cache", "plugins/cache", true, false)]
    // a slash in the middle anchors too
    [InlineData("plugins/*.jar", "plugins/a.jar", false, true)]
    [InlineData("plugins/*.jar", "x/plugins/a.jar", false, false)]
    [InlineData("plugins/*.jar", "plugins/sub/a.jar", false, false)]
    // **
    [InlineData("**/config.yml", "config.yml", false, true)]
    [InlineData("**/config.yml", "a/b/config.yml", false, true)]
    [InlineData("world/**", "world/region/r.0.0.mca", false, true)]
    [InlineData("world/**", "world", true, false)]
    [InlineData("a/**/b", "a/b", false, true)]
    [InlineData("a/**/b", "a/x/y/b", false, true)]
    // trailing / = directories only
    [InlineData("tmp/", "tmp", true, true)]
    [InlineData("tmp/", "tmp", false, false)]
    // ? and classes
    [InlineData("r.?.mca", "r.1.mca", false, true)]
    [InlineData("r.[0-2].mca", "r.3.mca", false, false)]
    [InlineData("r.[!0-2].mca", "r.3.mca", false, true)]
    // * does not cross directories
    [InlineData("a*", "ab/c", false, false)]
    public void Matches(string pattern, string path, bool isDirectory, bool expected)
    {
        Assert.Equal(expected, IgnoreMatcher.Parse(pattern).Matches(path, isDirectory));
    }

    [Fact]
    public void Negation_ReIncludes_TheLastMatchWins()
    {
        var matcher = IgnoreMatcher.Parse("*.log\n!important.log\n# comment\n\n");

        Assert.True(matcher.Matches("latest.log", false));
        Assert.False(matcher.Matches("important.log", false));
        Assert.False(matcher.Matches("# comment", false));
    }

    [Fact]
    public void NothingInsideAnExcludedDirectory_CanBeReIncluded()
    {
        var matcher = IgnoreMatcher.Parse("logs/\n!logs/keep.txt");

        Assert.True(matcher.IsExcluded("logs/keep.txt", false));
        Assert.True(matcher.IsExcluded("logs/other.txt", false));
    }

    [Fact]
    public void Escapes_AndTrailingSpaces()
    {
        Assert.True(IgnoreMatcher.Parse("\\#notes").Matches("#notes", false));
        Assert.True(IgnoreMatcher.Parse("\\!bang").Matches("!bang", false));
        Assert.True(IgnoreMatcher.Parse("file.txt   ").Matches("file.txt", false));
    }

    [Fact]
    public void RegexCharacters_AreLiteral()
    {
        var matcher = IgnoreMatcher.Parse("a+b(1).txt");

        Assert.True(matcher.Matches("a+b(1).txt", false));
        Assert.False(matcher.Matches("aab1.txt", false));
    }
}

public class DenylistTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("server.properties", true)]
    [InlineData("config/server.properties", true)]
    [InlineData("secrets/key.pem", true)]
    [InlineData("plugins/a.jar", true)]
    [InlineData("plugins/lib/b.jar", false)]
    [InlineData("readme.txt", false)]
    public void Globs(string path, bool denied)
    {
        var denylist = IgnoreMatcher.Parse(["server.properties", "/secrets/", "/plugins/*.jar"]);
        var full = _temp.Write(path, "x");

        if (denied)
            Assert.Equal(403, Assert.Throws<ServerApiException>(() => ServerFiles.CheckDenylist(denylist, _temp.Path, full)).StatusCode);
        else
            ServerFiles.CheckDenylist(denylist, _temp.Path, full);
    }

    [Fact]
    public void LinksInsideOfTheVolume_DoNotBypassTheDenylist()
    {
        _temp.Write("secret.txt", "x");
        File.CreateSymbolicLink(_temp.Combine("innocent.txt"), "secret.txt");

        var denylist = IgnoreMatcher.Parse("secret.txt");

        Assert.Throws<ServerApiException>(() => ServerFiles.CheckDenylist(denylist, _temp.Path, _temp.Combine("innocent.txt")));
    }

    [Fact]
    public void Directories_AreCheckedRecursivelyForDeleteAndRename()
    {
        _temp.Write("config/nested/token.key", "x");
        var denylist = IgnoreMatcher.Parse("*.key");

        ServerFiles.CheckDenylist(denylist, _temp.Path, _temp.Combine("config"));
        Assert.Throws<ServerApiException>(() => ServerFiles.CheckDenylist(denylist, _temp.Path, _temp.Combine("config"), recursive: true));
    }
}

public class FileNameTests
{
    [Theory]
    [InlineData("server.properties", "server copy.properties")]
    [InlineData("backup.tar.gz", "backup copy.tar.gz")]
    [InlineData(".env", ".env copy")]
    [InlineData("README", "README copy")]
    public void FirstCopy(string name, string expected)
    {
        Assert.Equal(expected, FileNames.GetCopyName(name, _ => false));
    }

    [Fact]
    public void FurtherCopies_AreNumbered()
    {
        var taken = new HashSet<string> { "a copy.txt", "a copy 2.txt" };

        Assert.Equal("a copy 3.txt", FileNames.GetCopyName("a.txt", taken.Contains));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\file.txt", "file.txt")]
    [InlineData("..", null)]
    [InlineData("  ", null)]
    [InlineData("a\u0000b.txt", "ab.txt")]
    public void Sanitize(string input, string? expected)
    {
        Assert.Equal(expected, FileNames.Sanitize(input));
    }

    [Fact]
    public void ArchiveNames_AreUnique()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 5, TimeSpan.Zero);

        Assert.Equal("archive-2026-10-31T120005Z.tar.gz", FileNames.GetArchiveName(now, ".tar.gz", _ => false));
        Assert.Equal("archive-2026-10-31T120005Z-2.zip", FileNames.GetArchiveName(now, ".zip", name => name == "archive-2026-10-31T120005Z.zip"));
    }
}

public class FileModeTests
{
    [Theory]
    [InlineData(0b110_100_100, '-', "-rw-r--r--", "644")]
    [InlineData(0b111_101_101, 'd', "drwxr-xr-x", "755")]
    [InlineData(0b111_111_111, 'l', "lrwxrwxrwx", "777")]
    [InlineData(0b100_111_101_101, '-', "-rwsr-xr-x", "4755")]
    [InlineData(0b001_111_111_110, 'd', "drwxrwxrwT", "1776")]
    [InlineData(0b010_110_100_000, '-', "-rw-r-S---", "2640")]
    public void Format(int mode, char type, string expected, string octal)
    {
        Assert.Equal(expected, FileModes.Format((UnixFileMode)mode, type));
        Assert.Equal(octal, FileModes.ToOctal((UnixFileMode)mode));
    }

    [Theory]
    [InlineData("644", 0b110_100_100)]
    [InlineData("0755", 0b111_101_101)]
    public void Parse(string value, int expected)
    {
        Assert.True(FileModes.TryParse(value, out var mode));
        Assert.Equal((UnixFileMode)expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("788")]
    [InlineData("12345")]
    [InlineData("rwx")]
    public void Parse_RejectsInvalidModes(string value)
    {
        Assert.False(FileModes.TryParse(value, out _));
    }
}

public class FileTypeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void KnownTextExtensions_AreEditable()
    {
        var path = _temp.Write("server.properties", "motd=hi");

        Assert.Equal((FileTypes.TextMime, true), FileTypes.Detect(path, 7));
        Assert.True(FileTypes.Detect(_temp.Write("config.yml", "a: 1"), 4).Editable);
        Assert.True(FileTypes.Detect(_temp.Write("data.json", "{}"), 2).Editable);
    }

    [Fact]
    public void LargeTextFiles_AreNotEditable()
    {
        var path = _temp.Write("latest.log", "x");

        Assert.False(FileTypes.Detect(path, FileTypes.MaxEditableBytes + 1).Editable);
    }

    [Fact]
    public void KnownBinaryTypes_AreNotEditable()
    {
        Assert.False(FileTypes.Detect(_temp.Write("server.jar", "PK"), 2).Editable);
        Assert.False(FileTypes.Detect(_temp.Write("icon.png", "x"), 1).Editable);
    }

    [Fact]
    public void UnknownExtensions_AreSniffed()
    {
        var text = _temp.Write("eula", "eula=true\nÄÖÜ");
        var binary = _temp.Combine("blob");
        File.WriteAllBytes(binary, [1, 2, 0, 3]);

        Assert.Equal((FileTypes.TextMime, true), FileTypes.Detect(text, new FileInfo(text).Length));
        Assert.Equal((FileTypes.BinaryMime, false), FileTypes.Detect(binary, 4));
    }

    [Fact]
    public void LooksLikeText_ToleratesACutCharacterOnlyWhenIncomplete()
    {
        var bytes = Encoding.UTF8.GetBytes("aä")[..2]; // "a" + first byte of "ä"

        Assert.True(FileTypes.LooksLikeText(bytes, complete: false));
        Assert.False(FileTypes.LooksLikeText(bytes, complete: true));
        Assert.False(FileTypes.LooksLikeText([0x61, 0x00], complete: true));
    }
}

public class PublicAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:c0a8:0101::1")]
    public void Blocks_PrivateAndSpecialAddresses(string address)
    {
        Assert.False(PublicAddressGuard.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    public void Allows_PublicAddresses(string address)
    {
        Assert.True(PublicAddressGuard.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Connections_ToLoopback_AreRefused()
    {
        using var client = new HttpClient(PublicAddressGuard.CreateHandler());

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/"));
        Assert.True(error is BlockedAddressException || error.InnerException is BlockedAddressException);

        error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://localhost:9/"));
        Assert.True(error is BlockedAddressException || error.InnerException is BlockedAddressException);
    }
}

public class ArchiveTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _destination;

    public ArchiveTests()
    {
        _destination = Directory.CreateDirectory(_temp.Combine("volume", "target")).FullName;
    }

    public void Dispose() => _temp.Dispose();

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = _temp.Combine("test.zip");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return path;
    }

    private string Tar(Action<TarWriter> write, bool gzip = false)
    {
        var path = _temp.Combine(gzip ? "test.tar.gz" : "test.tar");

        using var file = File.Create(path);
        using Stream stream = gzip ? new GZipStream(file, CompressionLevel.Fastest) : file;
        using (var tar = new TarWriter(stream, TarEntryFormat.Pax))
            write(tar);

        return path;
    }

    private static PaxTarEntry FileEntry(string name, string content) => new(TarEntryType.RegularFile, name)
    {
        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
    };

    private void AssertNothingOutside()
    {
        Assert.False(File.Exists(_temp.Combine("volume", "evil.txt")));
        Assert.False(File.Exists(_temp.Combine("evil.txt")));
        Assert.False(File.Exists("/tmp/midnight-evil.txt"));
    }

    [Fact]
    public async Task Zip_SlipEntries_AreNotExtracted()
    {
        var zip = Zip(("ok/a.txt", "a"), ("../evil.txt", "x"), ("ok/../../evil.txt", "x"), ("..\\evil.txt", "x"));

        var result = await ArchiveExtractor.ExtractAsync(zip, ArchiveFormat.Zip, _destination, new ExtractOptions(), CancellationToken.None);

        Assert.Equal("a", File.ReadAllText(Path.Combine(_destination, "ok", "a.txt")));
        Assert.Equal(3, result.Unsafe);
        AssertNothingOutside();
    }

    [Fact]
    public async Task Tar_SlipEntries_AndEscapingLinks_AreNotExtracted()
    {
        var tar = Tar(writer =>
        {
            writer.WriteEntry(FileEntry("ok.txt", "ok"));
            writer.WriteEntry(FileEntry("../evil.txt", "x"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "abs") { LinkName = "/tmp" });
            writer.WriteEntry(FileEntry("abs/midnight-evil.txt", "x"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "up") { LinkName = "../.." });
            writer.WriteEntry(FileEntry("up/evil.txt", "x"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "/etc/passwd" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "inside") { LinkName = "ok.txt" });
        }, gzip: true);

        var result = await ArchiveExtractor.ExtractAsync(tar, ArchiveFormat.TarGz, _destination, new ExtractOptions(), CancellationToken.None);

        Assert.Equal("ok", File.ReadAllText(Path.Combine(_destination, "ok.txt")));
        Assert.Equal("ok.txt", new FileInfo(Path.Combine(_destination, "inside")).LinkTarget);
        // the refused link is not created, the following entry lands in a plain directory inside
        Assert.Null(new FileInfo(Path.Combine(_destination, "abs")).LinkTarget);
        Assert.Null(new FileInfo(Path.Combine(_destination, "up")).LinkTarget);
        Assert.False(Path.Exists(Path.Combine(_destination, "hard")));
        Assert.True(result.Unsafe >= 4);
        AssertNothingOutside();
    }

    [Fact]
    public async Task ExistingLinks_AreReplaced_NotWrittenThrough()
    {
        var outside = _temp.Write("outside.txt", "keep");
        File.CreateSymbolicLink(Path.Combine(_destination, "config.txt"), outside);
        var tar = Tar(writer => writer.WriteEntry(FileEntry("config.txt", "new")));

        var result = await ArchiveExtractor.ExtractAsync(tar, ArchiveFormat.Tar, _destination, new ExtractOptions(), CancellationToken.None);

        // the link points outside: the entry is refused and the outside file untouched
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal(1, result.Unsafe);
    }

    [Fact]
    public async Task SizeLimit_IsCountedFromTheRealData()
    {
        var zip = Zip(("big.txt", new string('x', 10_000)));

        await Assert.ThrowsAsync<SizeLimitExceededException>(() =>
            ArchiveExtractor.ExtractAsync(zip, ArchiveFormat.Zip, _destination, new ExtractOptions { MaxBytes = 5_000 }, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_destination, "big.txt")));
    }

    [Fact]
    public async Task Gz_ExtractsTheSingleFile()
    {
        var path = _temp.Combine("latest.log.gz");

        await using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("log line"));

        Assert.Equal(ArchiveFormat.Gz, ArchiveExtractor.DetectFormat(path));
        await ArchiveExtractor.ExtractAsync(path, ArchiveFormat.Gz, _destination, new ExtractOptions(), CancellationToken.None);

        Assert.Equal("log line", File.ReadAllText(Path.Combine(_destination, "latest.log")));
    }

    [Fact]
    public void Formats_AreDetectedByNameAndContent()
    {
        var zip = Zip(("a.txt", "a"));
        var renamed = _temp.Combine("download.bin");
        File.Copy(zip, renamed);

        Assert.Equal(ArchiveFormat.Zip, ArchiveExtractor.DetectFormat(zip));
        Assert.Equal(ArchiveFormat.Zip, ArchiveExtractor.DetectFormat(renamed));
        Assert.Equal(ArchiveFormat.TarGz, ArchiveExtractor.DetectFormat(_temp.Write("x.tgz", "")));
        Assert.Null(ArchiveExtractor.DetectFormat(_temp.Write("notes.bin", "hello")));
    }

    [Fact]
    public async Task Zip_Roundtrip_KeepsDirectoriesAndSkipsLinks()
    {
        var source = Directory.CreateDirectory(_temp.Combine("source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.CreateSymbolicLink(Path.Combine(source, "link"), "a.txt");
        var zip = _temp.Combine("out.zip");

        await using (var output = File.Create(zip))
            await ArchiveWriter.WriteZipAsync(output, ArchiveWriter.Walk(source, [""]), CancellationToken.None);

        await ArchiveExtractor.ExtractAsync(zip, ArchiveFormat.Zip, _destination, new ExtractOptions(), CancellationToken.None);

        Assert.Equal("a", File.ReadAllText(Path.Combine(_destination, "a.txt")));
        Assert.True(Directory.Exists(Path.Combine(_destination, "empty")));
        Assert.False(Path.Exists(Path.Combine(_destination, "link")));
    }
}

/// <summary>The file manager on a real temp volume, the docker engine is never contacted</summary>
public class ServerFilesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerFileSystem _fileSystem;
    private readonly ServerFiles _files;
    private readonly ServerInstance _server;
    private readonly string _volume;

    public ServerFilesTests()
    {
        var configuration = new DemonConfiguration { DataPath = _temp.Path, DockerSocket = "unix:///nonexistent/docker.sock", Servers = { ContainerUser = "" } };
        _fileSystem = new ServerFileSystem(configuration);
        _files = new ServerFiles(_fileSystem, configuration, NullLogger<ServerFiles>.Instance, new FakeLifetime());

        var serverConfiguration = new DemonServerConfiguration { Uuid = Guid.NewGuid(), Name = "test" };
        serverConfiguration.Limits.Disk = 1; // MiB
        serverConfiguration.Process.Features.FileDenylist = ["*.secret"];

        _server = new ServerInstance(serverConfiguration, new DockerService(configuration, NullLogger<DockerService>.Instance), _fileSystem,
            configuration.Servers, NullLogger.Instance, CancellationToken.None);
        _volume = _fileSystem.EnsureVolume(_server.Uuid);
    }

    public void Dispose() => _temp.Dispose();

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_volume, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void List_DescribesFiles_DirectoriesFirst()
    {
        Write("b.txt", "hello");
        Directory.CreateDirectory(Path.Combine(_volume, "zdir"));

        var entries = _files.List(_server, "/");

        Assert.Equal(["zdir", "b.txt"], entries.Select(x => x.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.Equal(5, entries[1].Size);
        Assert.True(entries[1].IsEditable);
        Assert.Equal(10, entries[1].Mode.Length);
        Assert.Matches("^[0-7]{3,4}$", entries[1].ModeBits);
    }

    [Fact]
    public void List_UnknownDirectory_Is404()
    {
        Assert.Equal(404, Assert.Throws<ServerApiException>(() => _files.List(_server, "/missing")).StatusCode);
    }

    [Fact]
    public async Task Contents_RejectsBinaryAndDeniedFiles()
    {
        File.WriteAllBytes(Path.Combine(_volume, "blob.bin"), [0, 1, 2]);
        Write("token.secret", "x");
        Write("ok.txt", "text");

        Assert.Equal("text", Encoding.UTF8.GetString(await _files.ReadContentsAsync(_server, "/ok.txt", CancellationToken.None)));
        Assert.Equal(400, (await Assert.ThrowsAsync<ServerApiException>(() => _files.ReadContentsAsync(_server, "blob.bin", CancellationToken.None))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ServerApiException>(() => _files.ReadContentsAsync(_server, "token.secret", CancellationToken.None))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ServerApiException>(() => _files.ReadContentsAsync(_server, "nope.txt", CancellationToken.None))).StatusCode);
    }

    [Fact]
    public void Copy_NamesTheCopies_AndRespectsTheDiskLimit()
    {
        Write("config/a.yml", "x: 1");

        _files.Copy(_server, new CopyFileRequest { Location = "/config/a.yml" });
        _files.Copy(_server, new CopyFileRequest { Location = "/config/a.yml" });

        Assert.True(File.Exists(Path.Combine(_volume, "config", "a copy.yml")));
        Assert.True(File.Exists(Path.Combine(_volume, "config", "a copy 2.yml")));

        Write("big.dat", new string('x', 600_000));
        _server.AddDiskBytes(600_000);

        Assert.Equal(413, Assert.Throws<ServerApiException>(() => _files.Copy(_server, new CopyFileRequest { Location = "big.dat" })).StatusCode);
    }

    [Fact]
    public void Rename_MovesAndRejectsConflictsAndEscapes()
    {
        Write("a.txt", "a");
        Write("b.txt", "b");

        _files.Rename(_server, new RenameFilesRequest { Root = "/", Files = [new FileRename { From = "a.txt", To = "sub/c.txt" }] });
        Assert.True(File.Exists(Path.Combine(_volume, "sub", "c.txt")));

        Assert.Equal(409, Assert.Throws<ServerApiException>(() =>
            _files.Rename(_server, new RenameFilesRequest { Root = "/sub", Files = [new FileRename { From = "c.txt", To = "../b.txt" }] })).StatusCode);
        Assert.Equal(403, Assert.Throws<ServerApiException>(() =>
            _files.Rename(_server, new RenameFilesRequest { Root = "/", Files = [new FileRename { From = "b.txt", To = "../../b.txt" }] })).StatusCode);
        Assert.Equal(403, Assert.Throws<ServerApiException>(() =>
            _files.Rename(_server, new RenameFilesRequest { Root = "/", Files = [new FileRename { From = "b.txt", To = "b.secret" }] })).StatusCode);
    }

    [Fact]
    public void Delete_RemovesLinksButNotTheirTargets()
    {
        var target = Write("data/keep.txt", "keep");
        Directory.CreateSymbolicLink(Path.Combine(_volume, "alias"), "data");

        _files.Delete(_server, new DeleteFilesRequest { Root = "/", Files = ["alias"] });

        Assert.True(File.Exists(target));
        Assert.False(Path.Exists(Path.Combine(_volume, "alias")));
        Assert.Equal(404, Assert.Throws<ServerApiException>(() => _files.Delete(_server, new DeleteFilesRequest { Files = ["missing"] })).StatusCode);
    }

    [Fact]
    public void CreateFolder_AndChmod()
    {
        _files.CreateFolder(_server, new CreateFolderRequest { Root = "/", Name = "plugins/config" });
        Assert.True(Directory.Exists(Path.Combine(_volume, "plugins", "config")));
        Assert.Equal(409, Assert.Throws<ServerApiException>(() => _files.CreateFolder(_server, new CreateFolderRequest { Name = "plugins" })).StatusCode);

        if (OperatingSystem.IsWindows())
            return;

        var script = Write("start.sh", "echo hi");
        _files.Chmod(_server, new ChmodFilesRequest { Root = "/", Files = [new FileChmod { File = "start.sh", Mode = "4755" }] });

        // setuid is dropped
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(script));
    }

    [Fact]
    public async Task CompressAndDecompress_Roundtrip()
    {
        Write("world/level.dat", "level");
        Write("world/region/r.0.0.mca", "region");
        Write("world/token.secret", "secret");

        var archive = await _files.CompressAsync(_server, new CompressFilesRequest { Root = "/", Files = ["world"], Format = "tar.gz" });

        Assert.StartsWith("archive-", archive.Name);
        Assert.EndsWith(".tar.gz", archive.Name);

        Directory.CreateDirectory(Path.Combine(_volume, "restore"));
        File.Move(Path.Combine(_volume, archive.Name), Path.Combine(_volume, "restore", archive.Name));

        await _files.DecompressAsync(_server, new DecompressFileRequest { Root = "/restore", File = archive.Name });

        Assert.Equal("region", File.ReadAllText(Path.Combine(_volume, "restore", "world", "region", "r.0.0.mca")));
        // denied files are neither packed nor extracted
        Assert.False(File.Exists(Path.Combine(_volume, "restore", "world", "token.secret")));
    }

    [Fact]
    public async Task Decompress_StopsAtTheDiskLimit()
    {
        var zip = Path.Combine(_volume, "bomb.zip");

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var stream = archive.CreateEntry("big.bin").Open();
            await stream.WriteAsync(new byte[2 * 1024 * 1024]); // compresses to a few KiB, the limit is 1 MiB
        }

        var error = await Assert.ThrowsAsync<ServerApiException>(() => _files.DecompressAsync(_server, new DecompressFileRequest { Root = "/", File = "bomb.zip" }));

        Assert.Equal(413, error.StatusCode);
        Assert.False(File.Exists(Path.Combine(_volume, "big.bin")));
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
