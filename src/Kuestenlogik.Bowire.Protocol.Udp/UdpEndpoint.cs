// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Kuestenlogik.Bowire.Protocol.Udp;

/// <summary>
/// URL parser and socket factory for <c>udp://host:port</c> addresses.
/// Classifies the configured address into multicast / broadcast /
/// unicast so the listen socket joins a group when needed and sets
/// <c>SO_BROADCAST</c> when needed, but never both. The OS delivers
/// traffic for all three shapes once the socket is bound to
/// <see cref="IPAddress.Any"/> on the port.
/// </summary>
internal static class UdpEndpoint
{
    /// <summary>Default listen port when the URL omits one.</summary>
    public const int DefaultPort = 3000;

    /// <summary>How the UDP traffic is delivered.</summary>
    public enum TransportMode
    {
        /// <summary>IPv4 multicast address (224.0.0.0/4) — joins the group.</summary>
        Multicast,

        /// <summary>Limited (255.255.255.255) or subnet-directed (x.x.x.255) broadcast.</summary>
        Broadcast,

        /// <summary>Unicast listen — binds to the port and receives whatever arrives.</summary>
        Unicast,
    }

    /// <summary>Parsed listen coordinates.</summary>
    public readonly record struct Endpoint(IPAddress Address, int Port, TransportMode Mode);

    /// <summary>
    /// Parse <paramref name="serverUrl"/> as <c>udp://host:port</c> (or
    /// bare <c>host:port</c>). Accepts the hostnames <c>broadcast</c>
    /// (→ 255.255.255.255) and <c>multicast</c> (→ 239.1.2.3) as CLI
    /// shortcuts. Returns <c>null</c> when the URL doesn't look like a
    /// UDP address.
    /// </summary>
    public static Endpoint? TryParse(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;

        var trimmed = serverUrl.TrimStart();
        if (trimmed.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["udp://".Length..];
        else if (trimmed.Contains("://", StringComparison.Ordinal))
            return null; // some other scheme — not UDP.

        var hostPart = trimmed;
        var port = DefaultPort;
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0)
        {
            hostPart = trimmed[..colon];
            var portPart = trimmed[(colon + 1)..].TrimEnd('/');
            if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                port = DefaultPort;
        }

        hostPart = hostPart.TrimEnd('/');
        if (string.IsNullOrEmpty(hostPart)) return null;

        if (hostPart.Equals("broadcast", StringComparison.OrdinalIgnoreCase))
            hostPart = "255.255.255.255";
        else if (hostPart.Equals("multicast", StringComparison.OrdinalIgnoreCase))
            hostPart = "239.1.2.3";

        if (!IPAddress.TryParse(hostPart, out var address)) return null;

        return new Endpoint(address, port, ClassifyAddress(address));
    }

    /// <summary>Return the transport mode for an IPv4 address.</summary>
    internal static TransportMode ClassifyAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return TransportMode.Unicast;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return TransportMode.Unicast;

        // IPv4 multicast range per RFC 5771.
        if (bytes[0] >= 224 && bytes[0] <= 239) return TransportMode.Multicast;

        if (address.Equals(IPAddress.Broadcast)) return TransportMode.Broadcast;
        if (bytes[3] == 255) return TransportMode.Broadcast;

        return TransportMode.Unicast;
    }

    /// <summary>
    /// Open a UDP socket ready to receive datagrams for the given
    /// endpoint. Returns <c>null</c> when the OS refuses the bind or
    /// join. Sets <paramref name="joinedGroup"/> so the caller knows
    /// whether to DropMulticastGroup on cleanup.
    /// </summary>
    internal static UdpClient? CreateListenSocket(Endpoint endpoint, out bool joinedGroup)
    {
        joinedGroup = false;
        var socket = new UdpClient(AddressFamily.InterNetwork)
        {
            ExclusiveAddressUse = false,
        };
        socket.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (endpoint.Mode == TransportMode.Broadcast)
        {
            try
            {
                socket.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            }
            catch (SocketException) { /* best-effort */ }
        }

        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, endpoint.Port));
            if (endpoint.Mode == TransportMode.Multicast)
            {
                socket.JoinMulticastGroup(endpoint.Address);
                joinedGroup = true;
            }
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
        return socket;
    }
}
