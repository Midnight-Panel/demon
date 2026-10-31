namespace Midnight.Demon.Features.Servers;

/// <summary>An expected error of the server api, written as problem details with the status code</summary>
public class ServerApiException(int statusCode, string title, string? detail = null) : Exception(detail ?? title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    public static ServerApiException NotFound(Guid uuid) => new(StatusCodes.Status404NotFound, "Server not found", $"The server {uuid} does not exist on this node");
    public static ServerApiException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static ServerApiException BadRequest(string title, string? detail = null) => new(StatusCodes.Status400BadRequest, title, detail);
    public static ServerApiException Forbidden(string title, string? detail = null) => new(StatusCodes.Status403Forbidden, title, detail);
    public static ServerApiException FileNotFound(string path) => new(StatusCodes.Status404NotFound, "File not found", $"'{path}' does not exist");
    public static ServerApiException TooLarge(string title, string? detail = null) => new(StatusCodes.Status413PayloadTooLarge, title, detail);
}
