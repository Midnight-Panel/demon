using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Core.Http;
using Midnight.Demon.Core.Logging;
using Midnight.Demon.Core.Panel;
using Midnight.Demon.Features.Docker;
using Midnight.Demon.Features.Host;
using Midnight.Demon.Features.Servers;
using Midnight.Demon.Features.Servers.Backups;
using Midnight.Demon.Features.Servers.Files;
using Midnight.Shared.Http;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Protocol;
using Serilog;

var configPath = Environment.GetEnvironmentVariable("MIDNIGHT_DEMON_CONFIG") ?? DemonConfiguration.DefaultPath;
var configuration = DemonConfiguration.Load(configPath);

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
builder.Services.AddSingleton<ServerFileSystem>();
builder.Services.AddSingleton<ServerInstaller>();
builder.Services.AddSingleton<ServerFiles>();
builder.Services.AddSingleton<FilePuller>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddHostedService<BootSyncService>();
builder.Services.AddHostedService<DiskUsageService>();
builder.Services.AddExceptionHandler<ServerExceptionHandler>();
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
// the token check runs before the websocket upgrade, /api/servers/{uuid}/ws is protected like every other route
app.UseMiddleware<NodeTokenMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "ok",
    Version = typeof(DemonConfiguration).Assembly.GetName().Version?.ToString(3),
    Protocol = MidnightProtocol.Version
}));

app.MapGet("/api/system/info", (SystemInfoService system, CancellationToken cancellationToken) => system.GetAsync(cancellationToken));

app.MapGet("/api/system/logs", (LogBuffer logs, int? lines) =>
    new DemonLogsResponse { Lines = logs.GetLines(Math.Clamp(lines ?? 200, 1, LogBuffer.Capacity)) });

app.MapServerEndpoints();
app.MapFileEndpoints();
app.MapBackupEndpoints();

app.Logger.LogInformation("Midnight demon listening on port {Port}, panel {PanelUrl}", configuration.HttpPort, configuration.PanelUrl);

await app.RunAsync();
return 0;
