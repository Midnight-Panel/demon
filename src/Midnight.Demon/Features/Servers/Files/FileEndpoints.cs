using System.ComponentModel.DataAnnotations;
using Midnight.Shared.Http.Servers;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>/api/servers/{uuid}/files on the demon, same paths and bodies as the panel (which forwards 1:1)</summary>
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var files = app.MapGroup("/api/servers/{uuid:guid}/files");

        files.MapGet("/list", (Guid uuid, string? directory, ServerManager manager, ServerFiles serverFiles) =>
            Task.Run(() => serverFiles.List(manager.GetRequired(uuid), directory ?? "/")));

        files.MapGet("/contents", async (Guid uuid, string? file, ServerManager manager, ServerFiles serverFiles, CancellationToken cancellationToken) =>
        {
            var bytes = await serverFiles.ReadContentsAsync(manager.GetRequired(uuid), Required(file, "file"), cancellationToken);
            return Results.Bytes(bytes, "text/plain; charset=utf-8");
        });

        files.MapPost("/write", async (Guid uuid, string? file, HttpRequest request, ServerManager manager, ServerFiles serverFiles, CancellationToken cancellationToken) =>
        {
            await serverFiles.WriteAsync(manager.GetRequired(uuid), Required(file, "file"), request, cancellationToken);
            return Results.NoContent();
        });

        files.MapPost("/create-folder", (Guid uuid, CreateFolderRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            serverFiles.CreateFolder(manager.GetRequired(uuid), Body(request));
            return Results.NoContent();
        });

        files.MapPut("/rename", (Guid uuid, RenameFilesRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            serverFiles.Rename(manager.GetRequired(uuid), Body(request));
            return Results.NoContent();
        });

        files.MapPost("/copy", (Guid uuid, CopyFileRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            serverFiles.Copy(manager.GetRequired(uuid), Body(request));
            return Results.NoContent();
        });

        files.MapPost("/delete", async (Guid uuid, DeleteFilesRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            var server = manager.GetRequired(uuid);
            var body = Body(request);
            await Task.Run(() => serverFiles.Delete(server, body));
            return Results.NoContent();
        });

        files.MapPost("/compress", (Guid uuid, CompressFilesRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            var body = Body(request);

            if (body.Format is not ("tar.gz" or "zip"))
                throw ServerApiException.BadRequest("Invalid format", "Expected tar.gz or zip");

            return serverFiles.CompressAsync(manager.GetRequired(uuid), body);
        });

        files.MapPost("/decompress", async (Guid uuid, DecompressFileRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            await serverFiles.DecompressAsync(manager.GetRequired(uuid), Body(request));
            return Results.NoContent();
        });

        files.MapPost("/chmod", (Guid uuid, ChmodFilesRequest? request, ServerManager manager, ServerFiles serverFiles) =>
        {
            serverFiles.Chmod(manager.GetRequired(uuid), Body(request));
            return Results.NoContent();
        });

        files.MapPost("/pull", (Guid uuid, PullFileRequest? request, ServerManager manager, FilePuller puller) =>
        {
            puller.Start(manager.GetRequired(uuid), Body(request));
            return Results.Accepted();
        });

        files.MapPost("/upload", async (Guid uuid, string? directory, HttpRequest request, ServerManager manager, ServerFiles serverFiles, CancellationToken cancellationToken) =>
        {
            await serverFiles.UploadAsync(manager.GetRequired(uuid), directory ?? "/", request, cancellationToken);
            return Results.NoContent();
        }).DisableAntiforgery();

        files.MapGet("/download", (Guid uuid, string? file, ServerManager manager, ServerFiles serverFiles) =>
        {
            var download = serverFiles.GetDownload(manager.GetRequired(uuid), Required(file, "file"));

            // size 0 may also be a fifo or device, those are never opened
            return download.Length == 0
                ? Results.File(Array.Empty<byte>(), download.Mime, download.Name)
                : Results.File(download.Path, download.Mime, download.Name, enableRangeProcessing: true);
        });

        return app;
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrEmpty(value) ? throw ServerApiException.BadRequest("Invalid request", $"The query parameter '{name}' is required") : value;

    /// <summary>The body, checked against its data annotations (400 with the problems)</summary>
    internal static T Body<T>(T? body) where T : class
    {
        if (body == null)
            throw ServerApiException.BadRequest("Invalid request", "The request body is missing");

        var results = new List<ValidationResult>();

        if (!Validator.TryValidateObject(body, new ValidationContext(body), results, validateAllProperties: true))
            throw ServerApiException.BadRequest("Invalid request", string.Join("; ", results.Select(x => x.ErrorMessage)));

        return body;
    }
}
