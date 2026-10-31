using System.Security.Cryptography;
using System.Text;
using Midnight.Daemon.Core.Configuration;
using Midnight.Shared.Protocol;

namespace Midnight.Daemon.Core.Http;

/// <summary>
/// Protects every /api route except /api/health with the node token from the configuration.
/// The panel sends "Authorization: Bearer mdn_{tokenId}.{token}".
/// </summary>
public class NodeTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DaemonConfiguration _configuration;
    private readonly ILogger<NodeTokenMiddleware> _logger;

    public NodeTokenMiddleware(RequestDelegate next, DaemonConfiguration configuration, ILogger<NodeTokenMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health"))
        {
            await _next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue(MidnightProtocol.HeaderName, out var protocol) && protocol.ToString() != MidnightProtocol.Version.ToString())
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, $"Unsupported protocol version {protocol}");
            return;
        }

        if (!IsAuthorized(context.Request.Headers.Authorization.ToString()))
        {
            _logger.LogWarning("Rejected request to {Path} with invalid token from {Ip}", path, context.Connection.RemoteIpAddress);
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Invalid node token");
            return;
        }

        await _next(context);
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string title)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { status, title }, options: null, contentType: "application/problem+json");
    }

    internal bool IsAuthorized(string header)
    {
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!NodeToken.TryParse(header["Bearer ".Length..].Trim(), out var tokenId, out var token))
            return false;

        var expected = Encoding.UTF8.GetBytes(NodeToken.Format(_configuration.TokenId, _configuration.Token));
        var actual = Encoding.UTF8.GetBytes(NodeToken.Format(tokenId, token));

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
