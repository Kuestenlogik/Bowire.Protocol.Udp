// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;

namespace Kuestenlogik.Bowire.Protocol.Udp.Tests;

public sealed class UdpEndpointTests
{
    [Fact]
    public void TryParse_UnicastUrl_MapsToUnicastMode()
    {
        var endpoint = UdpEndpoint.TryParse("udp://127.0.0.1:5514");
        Assert.NotNull(endpoint);
        Assert.Equal(UdpEndpoint.TransportMode.Unicast, endpoint!.Value.Mode);
        Assert.Equal(IPAddress.Loopback, endpoint.Value.Address);
        Assert.Equal(5514, endpoint.Value.Port);
    }

    [Fact]
    public void TryParse_MulticastUrl_MapsToMulticastMode()
    {
        var endpoint = UdpEndpoint.TryParse("udp://239.255.0.1:3000");
        Assert.NotNull(endpoint);
        Assert.Equal(UdpEndpoint.TransportMode.Multicast, endpoint!.Value.Mode);
    }

    [Fact]
    public void TryParse_LimitedBroadcast_MapsToBroadcastMode()
    {
        var endpoint = UdpEndpoint.TryParse("udp://255.255.255.255:3000");
        Assert.NotNull(endpoint);
        Assert.Equal(UdpEndpoint.TransportMode.Broadcast, endpoint!.Value.Mode);
    }

    [Fact]
    public void TryParse_BroadcastKeyword_MapsToLimitedBroadcast()
    {
        var endpoint = UdpEndpoint.TryParse("udp://broadcast:3000");
        Assert.NotNull(endpoint);
        Assert.Equal(UdpEndpoint.TransportMode.Broadcast, endpoint!.Value.Mode);
        Assert.Equal(IPAddress.Broadcast, endpoint.Value.Address);
    }

    [Fact]
    public void TryParse_MulticastKeyword_MapsToDefaultGroup()
    {
        var endpoint = UdpEndpoint.TryParse("udp://multicast");
        Assert.NotNull(endpoint);
        Assert.Equal(UdpEndpoint.TransportMode.Multicast, endpoint!.Value.Mode);
        Assert.Equal(UdpEndpoint.DefaultPort, endpoint.Value.Port);
    }

    [Fact]
    public void TryParse_HttpsUrl_ReturnsNull()
    {
        Assert.Null(UdpEndpoint.TryParse("https://example.com"));
    }

    [Fact]
    public void TryParse_Empty_ReturnsNull()
    {
        Assert.Null(UdpEndpoint.TryParse(""));
        Assert.Null(UdpEndpoint.TryParse(null));
    }

    [Fact]
    public void ClassifyAddress_SubnetBroadcast()
        => Assert.Equal(UdpEndpoint.TransportMode.Broadcast,
            UdpEndpoint.ClassifyAddress(IPAddress.Parse("192.168.1.255")));

    [Fact]
    public void ClassifyAddress_PrivateUnicast()
        => Assert.Equal(UdpEndpoint.TransportMode.Unicast,
            UdpEndpoint.ClassifyAddress(IPAddress.Parse("10.0.0.42")));
}
