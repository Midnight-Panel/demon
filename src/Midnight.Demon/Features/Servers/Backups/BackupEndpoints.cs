using Midnight.Shared.Http.Demon;

namespace Midnight.Demon.Features.Servers.Backups;

/// <summary>/api/servers/{uuid}/backups on the demon, called by the panel</summary>
public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var backups = app.MapGroup("/api/servers/{uuid:guid}/backups");

        backups.MapPost("/", (Guid uuid, DemonCreateBackupRequest? request, ServerManager manager, BackupService service) =>
        {
            if (request == null)
                throw ServerApiException.BadRequest("Invalid request", "The request body is missing");

            service.Create(manager.GetRequired(uuid), request);
            return Results.Accepted($"/api/servers/{uuid}/backups/{request.BackupUuid}");
        });

        backups.MapGet("/{backupUuid:guid}/download", (Guid uuid, Guid backupUuid, ServerManager manager, BackupService service) =>
        {
            var path = service.GetArchivePath(manager.GetRequired(uuid), backupUuid);
            return Results.File(path, "application/gzip", $"{backupUuid}.tar.gz", enableRangeProcessing: true);
        });

        backups.MapPost("/{backupUuid:guid}/restore", (Guid uuid, Guid backupUuid, DemonRestoreBackupRequest? request, ServerManager manager, BackupService service) =>
        {
            service.Restore(manager.GetRequired(uuid), backupUuid, request ?? new DemonRestoreBackupRequest());
            return Results.Accepted();
        });

        backups.MapDelete("/{backupUuid:guid}", (Guid uuid, Guid backupUuid, ServerManager manager, BackupService service) =>
        {
            service.Delete(manager.GetRequired(uuid), backupUuid);
            return Results.NoContent();
        });

        return app;
    }
}
