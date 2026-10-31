using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Docker.DotNet;

namespace Midnight.Demon.Features.Servers.Containers;

internal static class DockerErrors
{
    /// <summary>True when the exception means the docker engine could not be reached at all</summary>
    public static bool IsUnavailable(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException or HttpRequestException or TimeoutException)
                return true;
        }

        // Docker.DotNet reports its request timeout as a cancellation
        return exception is OperationCanceledException;
    }

    public static bool IsNotFound(Exception exception) =>
        exception is DockerContainerNotFoundException or DockerImageNotFoundException ||
        exception is DockerApiException { StatusCode: HttpStatusCode.NotFound };

    /// <summary>409 from docker: e.g. "container is not running" on kill</summary>
    public static bool IsConflict(Exception exception) =>
        exception is DockerApiException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.NotModified };

    /// <summary>A short message for users: the "message" of a docker error response or the exception message</summary>
    public static string Describe(Exception exception)
    {
        if (exception is DockerApiException api && !string.IsNullOrWhiteSpace(api.ResponseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(api.ResponseBody);

                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                    return message.GetString()!;
            }
            catch (JsonException)
            {
                // not json, use the body as it is
            }

            return api.ResponseBody.Trim();
        }

        if (exception is not DockerApiException && IsUnavailable(exception))
            return $"The docker engine is not reachable ({exception.Message})";

        return exception.Message;
    }
}
