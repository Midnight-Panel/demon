using System.Text;
using Docker.DotNet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Core.Http;
using Midnight.Demon.Features.Servers;
using Midnight.Shared.Enums;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Http.Servers;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Tests;

public class ServerFileSystemTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("midnight-data-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ServerFileSystem Files() => new(new DemonConfiguration { DataPath = _root, Servers = { ContainerUser = "" } });

    [Fact]
    public void Paths_AreBelowTheDataPath()
    {
        var files = Files();
        var uuid = Guid.NewGuid();

        Assert.Equal(Path.Combine(_root, "volumes", uuid.ToString()), files.GetVolumePath(uuid));
        Assert.Equal(Path.Combine(_root, "logs", "install", $"{uuid}.log"), files.GetInstallLogPath(uuid));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a/../../other")]
    [InlineData("..\\other")]
    [InlineData("")]
    [InlineData("/")]
    public void ResolveInside_RejectsPathsOutsideOfTheVolume(string path)
    {
        var volume = Files().EnsureVolume(Guid.NewGuid());

        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(volume, path));
    }

    [Fact]
    public void ResolveInside_AcceptsNestedPaths()
    {
        var volume = Files().EnsureVolume(Guid.NewGuid());

        Assert.Equal(Path.Combine(volume, "config", "a.yml"), ServerFileSystem.ResolveInside(volume, "/config/./a.yml"));
    }

    [Fact]
    public void ResolveInside_RejectsSymlinksLeavingTheVolume()
    {
        var volume = Files().EnsureVolume(Guid.NewGuid());
        var outside = Directory.CreateDirectory(Path.Combine(_root, "secret")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(volume, "link"), outside);
        File.CreateSymbolicLink(Path.Combine(volume, "file-link"), Path.Combine(outside, "passwd"));

        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(volume, "link/config.yml"));
        Assert.Throws<UnauthorizedAccessException>(() => ServerFileSystem.ResolveInside(volume, "file-link"));
    }

    [Fact]
    public void IsInside_DoesNotMatchSiblingPrefixes()
    {
        Assert.True(ServerFileSystem.IsInside("/data/volumes", "/data/volumes/abc"));
        Assert.False(ServerFileSystem.IsInside("/data/volumes", "/data/volumes2/abc"));
        Assert.False(ServerFileSystem.IsInside("/data/volumes", "/data"));
    }

    [Fact]
    public void DeleteVolume_RemovesTheVolume_ButNeverFollowsLinks()
    {
        var files = Files();
        var uuid = Guid.NewGuid();
        var volume = files.EnsureVolume(uuid);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "keep")).FullName;
        File.WriteAllText(Path.Combine(outside, "important.txt"), "x");
        File.WriteAllText(Path.Combine(volume, "world.dat"), "x");
        Directory.CreateSymbolicLink(Path.Combine(volume, "link"), outside);

        files.DeleteVolume(uuid);

        Assert.False(Directory.Exists(volume));
        Assert.True(File.Exists(Path.Combine(outside, "important.txt")));
    }

    [Fact]
    public void DirectorySize_SumsFiles()
    {
        var volume = Files().EnsureVolume(Guid.NewGuid());
        File.WriteAllBytes(Path.Combine(volume, "a"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(volume, "sub"));
        File.WriteAllBytes(Path.Combine(volume, "sub", "b"), new byte[50]);

        Assert.Equal(150, ServerFileSystem.GetDirectorySize(volume));
        Assert.Equal(0, ServerFileSystem.GetDirectorySize(Path.Combine(volume, "missing")));
    }
}

public class ServerConfigurationValidatorTests
{
    private static DemonServerConfiguration Valid() => new()
    {
        Uuid = Guid.NewGuid(),
        DockerImage = "debian",
        PrimaryAllocation = new DemonAllocation { IpAddress = "0.0.0.0", Port = 25565 },
        Allocations = [new DemonAllocation { IpAddress = "0.0.0.0", Port = 25565 }]
    };

    [Fact]
    public void AcceptsAValidConfiguration()
    {
        Assert.Empty(ServerConfigurationValidator.Validate(Valid()));
    }

    [Fact]
    public void ReportsProblems()
    {
        var configuration = Valid();
        configuration.Uuid = Guid.Empty;
        configuration.DockerImage = " ";
        configuration.Limits.Swap = -2;
        configuration.Limits.Threads = "0-a";
        configuration.Allocations.Add(new DemonAllocation { IpAddress = "nope", Port = 70000 });
        configuration.Install.Script = "echo";
        configuration.Install.Image = "";

        var errors = ServerConfigurationValidator.Validate(configuration);

        Assert.Equal(7, errors.Count);
        Assert.Single(ServerConfigurationValidator.Validate(null));
    }
}

public class ServerConsoleSocketTests
{
    private static ServerConsoleSocket.IncomingMessage Parse(string json) => ServerConsoleSocket.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Command()
    {
        var message = Parse("""{"event":"command","data":"say hi\n"}""");

        Assert.Null(message.Error);
        Assert.Equal("say hi", message.Command);
    }

    [Theory]
    [InlineData("start", PowerAction.Start)]
    [InlineData("Stop", PowerAction.Stop)]
    [InlineData("RESTART", PowerAction.Restart)]
    [InlineData(" kill ", PowerAction.Kill)]
    public void Power_IsCaseInsensitive(string action, PowerAction expected)
    {
        var message = Parse($$"""{"event":"power","data":"{{action}}"}""");

        Assert.Null(message.Error);
        Assert.Equal(expected, message.Power);
    }

    [Theory]
    [InlineData("""{"event":"power","data":"1"}""")]
    [InlineData("""{"event":"power","data":"explode"}""")]
    [InlineData("""{"event":"power","data":2}""")]
    [InlineData("""{"event":"command","data":""}""")]
    [InlineData("""{"event":"command","data":"a\nb"}""")]
    [InlineData("""{"event":"command","data":5}""")]
    [InlineData("""{"event":"history"}""")]
    [InlineData("""{"data":"x"}""")]
    [InlineData("""["command"]""")]
    [InlineData("not json")]
    public void BadInput_IsAnError(string json)
    {
        Assert.NotNull(Parse(json).Error);
    }

    [Fact]
    public void TooLongCommand_IsAnError()
    {
        Assert.NotNull(ServerConsoleSocket.ValidateCommand(new string('a', ServerConsoleSocket.MaxCommandLength + 1)).Error);
    }

    [Fact]
    public void Events_AreSerializedWithJsonDefaults()
    {
        Assert.Equal("""{"event":"status","data":"Online"}""",
            Encoding.UTF8.GetString(ServerConsoleSocket.Serialize(new ConsoleMessage { Event = ConsoleEvents.Status, Data = ServerState.Online })));

        var stats = Encoding.UTF8.GetString(ServerConsoleSocket.Serialize(new ConsoleMessage { Event = ConsoleEvents.Stats, Data = new ServerStatsDto { CpuPercent = 12.5 } }));
        Assert.Contains("\"cpuPercent\":12.5", stats);

        Assert.Equal("""{"event":"history","data":["a","b"]}""",
            Encoding.UTF8.GetString(ServerConsoleSocket.Serialize(new ConsoleMessage { Event = ConsoleEvents.History, Data = new List<string> { "a", "b" } })));
    }
}

public class ServerExceptionHandlerTests
{
    [Fact]
    public void ApiExceptions_KeepTheirStatus()
    {
        var problem = ServerExceptionHandler.ToProblem(ServerApiException.NotFound(Guid.Empty))!;

        Assert.Equal(404, problem.Status);
        Assert.Equal("Server not found", problem.Title);
        Assert.Equal(409, ServerExceptionHandler.ToProblem(ServerApiException.Conflict("The server is not running"))!.Status);
    }

    [Fact]
    public void UnreachableDocker_Is503()
    {
        var exception = new HttpRequestException("connect failed", new System.Net.Sockets.SocketException(2));

        Assert.Equal(503, ServerExceptionHandler.ToProblem(exception)!.Status);
    }

    [Fact]
    public void DockerErrors_Are502_WithDockersMessage()
    {
        var exception = new DockerApiException(System.Net.HttpStatusCode.InternalServerError, """{"message":"port is already allocated"}""");
        var problem = ServerExceptionHandler.ToProblem(exception)!;

        Assert.Equal(502, problem.Status);
        Assert.Equal("port is already allocated", problem.Detail);
    }

    [Fact]
    public void OtherExceptions_AreLeftToTheDefaultHandler()
    {
        Assert.Null(ServerExceptionHandler.ToProblem(new InvalidOperationException("bug")));
    }
}

public class ServerRouteProtectionTests
{
    private static NodeTokenMiddleware Create(Action onNext) => new(
        _ =>
        {
            onNext();
            return Task.CompletedTask;
        },
        new DemonConfiguration { TokenId = "id123", Token = "secret" },
        NullLogger<NodeTokenMiddleware>.Instance);

    [Theory]
    [InlineData("GET", "/api/servers")]
    [InlineData("POST", "/api/servers")]
    [InlineData("GET", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00")]
    [InlineData("DELETE", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00")]
    [InlineData("POST", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00/power")]
    [InlineData("POST", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00/commands")]
    [InlineData("GET", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00/logs")]
    public async Task ServerRoutes_RequireTheToken(string method, string path)
    {
        var called = false;
        var context = Context(method, path);

        await Create(() => called = true).InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task WebsocketUpgrade_RequiresTheToken()
    {
        var called = false;
        var context = Context("GET", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00/ws");
        context.Request.Headers.Connection = "Upgrade";
        context.Request.Headers.Upgrade = "websocket";
        context.Request.Headers.SecWebSocketVersion = "13";
        context.Request.Headers.SecWebSocketKey = "dGhlIHNhbXBsZSBub25jZQ==";

        await Create(() => called = true).InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);

        // with the token the upgrade reaches the endpoint
        var authorized = Context("GET", "/api/servers/4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00/ws");
        authorized.Request.Headers.Authorization = "Bearer mdn_id123.secret";

        await Create(() => called = true).InvokeAsync(authorized);

        Assert.True(called);
    }

    [Fact]
    public async Task WrongToken_IsRejected_HealthIsPublic()
    {
        var called = 0;
        var wrong = Context("GET", "/api/servers");
        wrong.Request.Headers.Authorization = "Bearer mdn_id123.wrong";

        await Create(() => called++).InvokeAsync(wrong);
        Assert.Equal(401, wrong.Response.StatusCode);

        await Create(() => called++).InvokeAsync(Context("GET", "/api/health"));
        Assert.Equal(1, called);
    }

    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext { RequestServices = new EmptyServices() };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
