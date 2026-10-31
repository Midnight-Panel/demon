using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Midnight.Shared.Enums;
using Midnight.Shared.Http;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Features.Servers;

/// <summary>
/// GET /api/servers/{uuid}/ws: the console of a server for the panel proxy (ConsoleProtocol).
/// On connect: history, status (+ stats while running), then the live events. Incoming: command and power.
/// </summary>
public static class ServerConsoleSocket
{
    /// <summary>Largest message accepted from the panel (the panel itself allows 16 KB per browser message)</summary>
    public const int MaxIncomingBytes = 64 * 1024;

    /// <summary>Longest command written to stdin</summary>
    public const int MaxCommandLength = 4096;

    public static async Task HandleAsync(HttpContext context, Guid uuid, ServerManager manager, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        // 404 before the upgrade, so the panel can tell the browser
        var server = manager.GetRequired(uuid);

        if (!context.WebSockets.IsWebSocketRequest)
            throw ServerApiException.BadRequest("Websocket expected", "Connect to this endpoint with a websocket");

        var logger = loggerFactory.CreateLogger(typeof(ServerConsoleSocket).FullName!);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        using var subscription = server.Subscribe(out var history);
        var token = cancellation.Token;

        logger.LogDebug("Console of server {Uuid} connected from {Ip}", uuid, context.Connection.RemoteIpAddress);

        try
        {
            await SendAsync(socket, new ConsoleMessage { Event = ConsoleEvents.History, Data = history }, token);
            await SendAsync(socket, new ConsoleMessage { Event = ConsoleEvents.Status, Data = server.State }, token);

            if (server.IsRunning)
                await SendAsync(socket, new ConsoleMessage { Event = ConsoleEvents.Stats, Data = server.GetStats() }, token);

            var send = SendLoopAsync(socket, subscription, token);
            var receive = ReceiveLoopAsync(socket, server, manager, subscription, logger, token);

            var finished = await Task.WhenAny(send, receive);
            var closeStatus = finished == send && send.IsCompletedSuccessfully && send.Result
                ? (WebSocketCloseStatus.NormalClosure, "The server has been deleted")
                : lifetime.ApplicationStopping.IsCancellationRequested
                    ? (WebSocketCloseStatus.EndpointUnavailable, "The demon is shutting down")
                    : (WebSocketCloseStatus.NormalClosure, "");

            cancellation.Cancel();
            await WhenAllQuietly(send, receive);
            await CloseQuietlyAsync(socket, closeStatus.Item1, closeStatus.Item2);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // the panel disconnected or the demon shuts down
        }
        finally
        {
            logger.LogDebug("Console of server {Uuid} disconnected", uuid);
        }
    }

    /// <summary>Sends the live events. True when the subscription ended because the server was deleted.</summary>
    private static async Task<bool> SendLoopAsync(WebSocket socket, ServerEventSubscription subscription, CancellationToken cancellationToken)
    {
        await foreach (var message in subscription.Reader.ReadAllAsync(cancellationToken))
            await SendAsync(socket, message, cancellationToken);

        return !cancellationToken.IsCancellationRequested;
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, ServerInstance server, ServerManager manager, ServerEventSubscription subscription,
        ILogger logger, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var message = new ArrayBufferWriter<byte>();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.Clear();
            WebSocketReceiveResult result;
            var tooLarge = false;

            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (message.WrittenCount + result.Count > MaxIncomingBytes)
                    tooLarge = true;
                else
                    message.Write(buffer.AsSpan(0, result.Count));
            } while (!result.EndOfMessage);

            if (tooLarge)
            {
                subscription.Write(ConsoleEvents.Error, $"Messages may not be larger than {MaxIncomingBytes / 1024} KB");
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                subscription.Write(ConsoleEvents.Error, "Only text messages are supported");
                continue;
            }

            var incoming = Parse(message.WrittenSpan);

            if (incoming.Error != null)
            {
                subscription.Write(ConsoleEvents.Error, incoming.Error);
                continue;
            }

            try
            {
                if (incoming.Event == ConsoleEvents.SendCommand)
                    await server.SendCommandAsync(incoming.Command!, cancellationToken);
                else
                    await manager.PowerAsync(server.Uuid, incoming.Power!.Value, cancellationToken);
            }
            catch (ServerApiException e)
            {
                subscription.Write(ConsoleEvents.Error, e.Detail ?? e.Title);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Console request {Event} of server {Uuid} failed", incoming.Event, server.Uuid);
                subscription.Write(ConsoleEvents.Error, $"The request failed: {e.Message}");
            }
        }
    }

    /// <summary>A parsed message of the panel: a command or a power action, or an error for the sender</summary>
    internal readonly record struct IncomingMessage(string? Event, string? Command, PowerAction? Power, string? Error);

    /// <summary>Parses {"event": "command"|"power", "data": "..."} (pure, used by the tests)</summary>
    internal static IncomingMessage Parse(ReadOnlySpan<byte> json)
    {
        string? @event;
        JsonElement data;

        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return new IncomingMessage(null, null, null, "Expected a json object like {\"event\": \"command\", \"data\": \"say hi\"}");

            @event = TryGetProperty(root, "event", out var eventElement) && eventElement.ValueKind == JsonValueKind.String ? eventElement.GetString() : null;
            data = TryGetProperty(root, "data", out var dataElement) ? dataElement.Clone() : default;
        }
        catch (JsonException)
        {
            return new IncomingMessage(null, null, null, "Invalid json");
        }

        switch (@event)
        {
            case ConsoleEvents.SendCommand:
            {
                if (data.ValueKind != JsonValueKind.String)
                    return new IncomingMessage(@event, null, null, "The data of a command must be a string");

                var command = ValidateCommand(data.GetString());
                return new IncomingMessage(@event, command.Value, null, command.Error);
            }

            case ConsoleEvents.SetPower:
            {
                var value = data.ValueKind == JsonValueKind.String ? data.GetString() : null;

                return TryParsePowerAction(value, out var action)
                    ? new IncomingMessage(@event, null, action, null)
                    : new IncomingMessage(@event, null, null, $"Unknown power action '{value}', expected Start, Stop, Restart or Kill");
            }

            case null:
                return new IncomingMessage(null, null, null, "The message has no event");

            default:
                return new IncomingMessage(@event, null, null, $"Unknown event '{@event}'");
        }
    }

    /// <summary>Power action by name, case insensitive. Numbers are rejected.</summary>
    internal static bool TryParsePowerAction(string? value, out PowerAction action)
    {
        action = default;
        var trimmed = value?.Trim();

        return !string.IsNullOrEmpty(trimmed) &&
               char.IsAsciiLetter(trimmed[0]) &&
               Enum.TryParse(trimmed, ignoreCase: true, out action) &&
               Enum.IsDefined(action);
    }

    /// <summary>A command for stdin: one line, not empty, not too long</summary>
    internal static (string? Value, string? Error) ValidateCommand(string? command)
    {
        var value = command?.TrimEnd('\r', '\n');

        if (string.IsNullOrWhiteSpace(value))
            return (null, "The command is empty");

        if (value.Length > MaxCommandLength)
            return (null, $"The command may not be longer than {MaxCommandLength} characters");

        if (value.Contains('\n') || value.Contains('\r'))
            return (null, "The command must be a single line");

        return (value, null);
    }

    internal static byte[] Serialize(ConsoleMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static Task SendAsync(WebSocket socket, ConsoleMessage message, CancellationToken cancellationToken) =>
        socket.SendAsync(Serialize(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    private static async Task WhenAllQuietly(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // expected when the connection ends
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(status, description, timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // the other side is gone already
        }
    }
}
