using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Core.Http;
using Midnight.Demon.Core.Logging;
using Midnight.Demon.Features.Host;
using Serilog.Events;
using Serilog.Parsing;

namespace Midnight.Demon.Tests;

public class NodeTokenMiddlewareTests
{
    private static NodeTokenMiddleware Create() => new(
        _ => Task.CompletedTask,
        new DemonConfiguration { TokenId = "id123", Token = "secret" },
        NullLogger<NodeTokenMiddleware>.Instance);

    [Fact]
    public void IsAuthorized_AcceptsMatchingToken()
    {
        Assert.True(Create().IsAuthorized("Bearer mdn_id123.secret"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer mdn_id123.wrong")]
    [InlineData("Bearer mdn_other.secret")]
    [InlineData("Basic mdn_id123.secret")]
    [InlineData("Bearer id123.secret")]
    public void IsAuthorized_RejectsEverythingElse(string header)
    {
        Assert.False(Create().IsAuthorized(header));
    }

    [Fact]
    public async Task Health_IsPublic_ApiIsProtected()
    {
        var middleware = Create();

        var health = new DefaultHttpContext();
        health.Request.Path = "/api/health";
        await middleware.InvokeAsync(health);
        Assert.Equal(200, health.Response.StatusCode);

        var info = new DefaultHttpContext { RequestServices = new EmptyServices() };
        info.Request.Path = "/api/system/info";
        info.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(info);
        Assert.Equal(401, info.Response.StatusCode);

        var other = new DefaultHttpContext { RequestServices = new EmptyServices() };
        other.Request.Path = "/metrics";
        other.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(other);
        Assert.Equal(401, other.Response.StatusCode);
    }

    private class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

public class ProcParserTests
{
    [Fact]
    public void ParseMeminfo_UsesAvailableMemory()
    {
        const string meminfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    6000000 kB\n";

        var (total, used) = SystemInfoService.ParseMeminfo(meminfo);

        Assert.Equal(16000000L * 1024, total);
        Assert.Equal(10000000L * 1024, used);
    }

    [Fact]
    public void CpuUsage_IsCalculatedBetweenSamples()
    {
        var first = CpuUsageSampler.Parse("cpu  100 0 100 700 100 0 0 0 0 0")!.Value;
        var second = CpuUsageSampler.Parse("cpu  150 0 150 750 150 0 0 0 0 0")!.Value;

        // 200 jiffies passed, 100 of them idle or iowait
        Assert.Equal(50, CpuUsageSampler.Calculate(first, second), 3);
    }

    [Fact]
    public void Parse_RejectsOtherLines()
    {
        Assert.Null(CpuUsageSampler.Parse("intr 12345"));
    }
}

public class LogBufferTests
{
    [Fact]
    public void KeepsOnlyTheLastLines()
    {
        var buffer = new LogBuffer();
        var parser = new MessageTemplateParser();

        for (var i = 0; i < LogBuffer.Capacity + 10; i++)
            buffer.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null, parser.Parse($"line {i}"), []));

        var lines = buffer.GetLines(LogBuffer.Capacity + 100);

        Assert.Equal(LogBuffer.Capacity, lines.Count);
        Assert.EndsWith($"line {LogBuffer.Capacity + 9}", lines[^1]);
        Assert.Equal(3, buffer.GetLines(3).Count);
    }
}
