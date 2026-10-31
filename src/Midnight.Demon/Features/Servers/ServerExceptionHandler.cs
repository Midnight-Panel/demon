using Docker.DotNet;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Demon.Features.Servers.Files;

namespace Midnight.Demon.Features.Servers;

/// <summary>
/// Writes expected errors as problem details: <see cref="ServerApiException"/> with its status code, an unreachable
/// docker engine as 503, other docker errors as 502 with docker's message, missing files as 404 and paths outside of
/// the volume as 403.
/// </summary>
public sealed class ServerExceptionHandler(IProblemDetailsService problemDetails, ILogger<ServerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var problem = ToProblem(exception, context.RequestAborted.IsCancellationRequested);

        if (problem == null)
            return false;

        if (problem.Status >= 500)
            logger.LogWarning("{Method} {Path} failed: {Title} {Detail}", context.Request.Method, context.Request.Path, problem.Title, problem.Detail);

        context.Response.StatusCode = problem.Status!.Value;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    /// <summary>The problem for an exception, null = unexpected (500 by the default handler)</summary>
    internal static ProblemDetails? ToProblem(Exception exception, bool requestAborted = false)
    {
        switch (exception)
        {
            case ServerApiException api:
                return new ProblemDetails { Status = api.StatusCode, Title = api.Title, Detail = api.Detail };

            case DockerApiException docker:
                return new ProblemDetails
                {
                    Status = StatusCodes.Status502BadGateway,
                    Title = "The docker engine rejected the request",
                    Detail = DockerErrors.Describe(docker)
                };

            case OperationCanceledException when requestAborted:
                return null;

            case BadHttpRequestException badRequest:
                return new ProblemDetails { Status = badRequest.StatusCode, Title = "Bad request", Detail = badRequest.Message };

            case SizeLimitExceededException:
                return new ProblemDetails { Status = StatusCodes.Status413PayloadTooLarge, Title = "Disk limit exceeded", Detail = exception.Message };

            case FileNotFoundException or DirectoryNotFoundException:
                return new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "File not found", Detail = "The file or directory does not exist" };

            case UnauthorizedAccessException:
                // permission errors of the file system, the message would contain the host path
                return new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Access denied", Detail = "The demon may not access this file" };

            default:
                if (DockerErrors.IsUnavailable(exception))
                {
                    return new ProblemDetails
                    {
                        Status = StatusCodes.Status503ServiceUnavailable,
                        Title = "The docker engine is not reachable",
                        Detail = exception.Message
                    };
                }

                return null;
        }
    }
}
