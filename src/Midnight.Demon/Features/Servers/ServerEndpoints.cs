using Midnight.Shared.Http.Demon;
using Midnight.Shared.Http.Servers;

namespace Midnight.Demon.Features.Servers;

/// <summary>/api/servers on the demon, called by the panel with the node token (see NodeTokenMiddleware)</summary>
public static class ServerEndpoints
{
    public static IEndpointRouteBuilder MapServerEndpoints(this IEndpointRouteBuilder app)
    {
        var servers = app.MapGroup("/api/servers");

        servers.MapGet("/", (ServerManager manager) =>
            manager.All.Select(server => server.GetState()).ToList());

        servers.MapGet("/{uuid:guid}", (Guid uuid, ServerManager manager) =>
            manager.GetRequired(uuid).GetState());

        servers.MapPost("/", (DemonCreateServerRequest? request, ServerManager manager) =>
        {
            if (request?.Configuration == null)
                throw ServerApiException.BadRequest("Invalid request", "The body must contain the server configuration");

            var server = manager.Create(request);
            return Results.Accepted($"/api/servers/{server.Uuid}", server.GetState());
        });

        servers.MapPut("/{uuid:guid}", async (Guid uuid, DemonServerConfiguration? configuration, ServerManager manager, CancellationToken cancellationToken) =>
        {
            if (configuration == null)
                throw ServerApiException.BadRequest("Invalid request", "The body must contain the server configuration");

            await manager.UpdateAsync(uuid, configuration, cancellationToken);
            return Results.NoContent();
        });

        servers.MapDelete("/{uuid:guid}", async (Guid uuid, ServerManager manager, CancellationToken cancellationToken) =>
        {
            await manager.DeleteAsync(uuid, cancellationToken);
            return Results.NoContent();
        });

        servers.MapPost("/{uuid:guid}/reinstall", (Guid uuid, ServerManager manager) =>
        {
            manager.Reinstall(uuid);
            return Results.Accepted($"/api/servers/{uuid}");
        });

        servers.MapPost("/{uuid:guid}/power", async (Guid uuid, PowerRequest? request, ServerManager manager, CancellationToken cancellationToken) =>
        {
            if (request == null || !Enum.IsDefined(request.Action))
                throw ServerApiException.BadRequest("Invalid power action", "Expected one of Start, Stop, Restart, Kill");

            await manager.PowerAsync(uuid, request.Action, cancellationToken);
            return Results.Accepted($"/api/servers/{uuid}");
        });

        servers.MapPost("/{uuid:guid}/commands", async (Guid uuid, CommandRequest? request, ServerManager manager, CancellationToken cancellationToken) =>
        {
            var command = ServerConsoleSocket.ValidateCommand(request?.Command);

            if (command.Error != null)
                throw ServerApiException.BadRequest("Invalid command", command.Error);

            await manager.GetRequired(uuid).SendCommandAsync(command.Value!, cancellationToken);
            return Results.NoContent();
        });

        servers.MapGet("/{uuid:guid}/logs", (Guid uuid, int? lines, ServerManager manager) =>
            new DemonConsoleLogResponse
            {
                Lines = manager.GetRequired(uuid).GetConsoleLines(Math.Clamp(lines ?? 100, 1, ServerInstance.HistoryLines))
            });

        servers.MapGet("/{uuid:guid}/ws", ServerConsoleSocket.HandleAsync);

        return app;
    }
}
