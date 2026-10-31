using System.Net;
using System.Net.Sockets;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>
/// SSRF protection for downloads from user supplied urls: only public unicast addresses may be contacted. The check
/// runs in the connect callback of the http handler on the resolved address that is then connected to, so a dns
/// answer can't change between the check and the connection (dns rebinding) and every redirect is checked again.
/// </summary>
public static class PublicAddressGuard
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8),         // "this" network
        (IPAddress.Parse("10.0.0.0"), 8),        // private
        (IPAddress.Parse("100.64.0.0"), 10),     // carrier grade nat
        (IPAddress.Parse("127.0.0.0"), 8),       // loopback
        (IPAddress.Parse("169.254.0.0"), 16),    // link local (cloud metadata)
        (IPAddress.Parse("172.16.0.0"), 12),     // private
        (IPAddress.Parse("192.0.0.0"), 24),      // ietf protocol assignments
        (IPAddress.Parse("192.0.2.0"), 24),      // documentation
        (IPAddress.Parse("192.88.99.0"), 24),    // 6to4 relay anycast
        (IPAddress.Parse("192.168.0.0"), 16),    // private
        (IPAddress.Parse("198.18.0.0"), 15),     // benchmarking
        (IPAddress.Parse("198.51.100.0"), 24),   // documentation
        (IPAddress.Parse("203.0.113.0"), 24),    // documentation
        (IPAddress.Parse("224.0.0.0"), 4),       // multicast
        (IPAddress.Parse("240.0.0.0"), 4)        // reserved and broadcast
    ];

    private static readonly (IPAddress Network, int Prefix)[] BlockedV6 =
    [
        (IPAddress.Parse("::"), 128),            // unspecified
        (IPAddress.Parse("::1"), 128),           // loopback
        (IPAddress.Parse("100::"), 64),          // discard
        (IPAddress.Parse("2001:db8::"), 32),     // documentation
        (IPAddress.Parse("fc00::"), 7),          // unique local
        (IPAddress.Parse("fe80::"), 10),         // link local
        (IPAddress.Parse("fec0::"), 10),         // site local (deprecated)
        (IPAddress.Parse("ff00::"), 8)           // multicast
    ];

    /// <summary>True when the address is public unicast and may be contacted</summary>
    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsAllowed(address.MapToIPv4());

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !BlockedV4.Any(range => InRange(address, range.Network, range.Prefix));

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if (BlockedV6.Any(range => InRange(address, range.Network, range.Prefix)))
            return false;

        var bytes = address.GetAddressBytes();

        // addresses that embed an ipv4 address are judged by it: ipv4 compatible (::a.b.c.d), nat64 (64:ff9b::/96), 6to4 (2002::/16)
        if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            return IsAllowed(new IPAddress(bytes.AsSpan(12, 4)));

        if (InRange(address, IPAddress.Parse("64:ff9b::"), 96))
            return IsAllowed(new IPAddress(bytes.AsSpan(12, 4)));

        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsAllowed(new IPAddress(bytes.AsSpan(2, 4)));

        return true;
    }

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
            return false;

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();

        for (var i = 0; i < a.Length && prefix > 0; i++, prefix -= 8)
        {
            var mask = prefix >= 8 ? 0xFF : (byte)(0xFF << (8 - prefix));

            if ((a[i] & mask) != (n[i] & mask))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A handler without proxy and automatic redirects whose connections only go to allowed addresses. The host is
    /// resolved here, when any of its addresses is not allowed the connection is refused (mixed answers are a
    /// rebinding trick), otherwise the checked address is connected to.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        UseCookies = false,
        ConnectCallback = ConnectAsync
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            addresses = [literal];
        else
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0)
            throw new HttpRequestException($"The host {host} could not be resolved");

        if (addresses.Any(address => !IsAllowed(address)))
            throw new BlockedAddressException($"The host {host} resolves to a private or reserved address");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // the addresses were checked above: connect to exactly these, no second lookup
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The url points to an address the demon may not contact</summary>
public sealed class BlockedAddressException(string message) : HttpRequestException(message);
