using Midnight.Daemon.Core.Configuration;
using Midnight.Daemon.Core.Http;
using Midnight.Daemon.Core.Logging;
using Midnight.Daemon.Core.Panel;
using Midnight.Daemon.Features.Docker;
using Midnight.Daemon.Features.Host;
using Midnight.Shared.Http;
using Midnight.Shared.Http.Daemon;
using Midnight.Shared.Protocol;
using Serilog;

var configPath = Environment.GetEnvironmentVariable("MIDNIGHT_DAEMON_CONFIG") ?? DaemonConfiguration.DefaultPath;
var configuration = DaemonConfiguration.Load(configPath);

var errors = configuration.Validate();

if (errors.Count > 0)
{
    Console.Error.WriteLine($"Invalid configuration in {configPath}:");
    errors.ForEach(error => Console.Error.WriteLine($"  - {error}"));
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

var logBuffer = new LogBuffer();

builder.Services.AddSerilog(logger => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.Sink(logBuffer));

builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(logBuffer);
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<CpuUsageSampler>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CpuUsageSampler>());
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddHttpClient<PanelClient>();
builder.Services.AddHostedService<BootSyncService>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => JsonDefaults.Apply(options.SerializerOptions));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(configuration.HttpPort, listen =>
    {
        if (configuration.Ssl.Enabled)
            listen.UseHttps(httpsOptions => httpsOptions.ServerCertificate =
                System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(
                    configuration.Ssl.CertificatePath,
                    configuration.Ssl.KeyPath));
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<NodeTokenMiddleware>();

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "ok",
    Version = typeof(DaemonConfiguration).Assembly.GetName().Version?.ToString(3),
    Protocol = MidnightProtocol.Version
}));

app.MapGet("/api/system/info", (SystemInfoService system, CancellationToken cancellationToken) => system.GetAsync(cancellationToken));

app.MapGet("/api/system/logs", (LogBuffer logs, int? lines) =>
    new DaemonLogsResponse { Lines = logs.GetLines(Math.Clamp(lines ?? 200, 1, LogBuffer.Capacity)) });

app.Logger.LogInformation("Midnight daemon listening on port {Port}, panel {PanelUrl}", configuration.HttpPort, configuration.PanelUrl);

await app.RunAsync();
return 0;
