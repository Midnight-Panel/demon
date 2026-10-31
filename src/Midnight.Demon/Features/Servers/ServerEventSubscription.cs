using System.Threading.Channels;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Features.Servers;

/// <summary>
/// Events of one server for one listener (a websocket). Bounded: a slow listener loses the oldest events instead of
/// slowing down the server or eating memory.
/// </summary>
public sealed class ServerEventSubscription : IDisposable
{
    public const int Capacity = 1000;

    private readonly ServerInstance _server;
    private readonly Channel<ConsoleMessage> _channel = Channel.CreateBounded<ConsoleMessage>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    internal ServerEventSubscription(ServerInstance server) => _server = server;

    public ChannelReader<ConsoleMessage> Reader => _channel.Reader;

    /// <summary>Queues a message for this listener only (e.g. an error answer)</summary>
    public bool Write(string @event, object? data) => _channel.Writer.TryWrite(new ConsoleMessage { Event = @event, Data = data });

    internal bool Write(ConsoleMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>Ends the subscription, the reader completes after the queued messages</summary>
    internal void Complete() => _channel.Writer.TryComplete();

    public void Dispose()
    {
        _server.Unsubscribe(this);
        Complete();
    }
}
