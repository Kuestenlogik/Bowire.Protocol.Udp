// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Protocol.Udp.Tests;

/// <summary>
/// Round-trip tests for <see cref="UdpMockEmitter"/>. Spin up a
/// loopback UDP receiver on an ephemeral port and drive the emitter
/// from a synthetic recording; assert the captured payloads arrive
/// verbatim on the wire in the expected order.
/// </summary>
public sealed class UdpMockEmitterTests
{
    [Fact]
    public async Task CanEmit_TrueWhenRecordingHasUdpStep()
    {
        await using var emitter = new UdpMockEmitter();
        var rec = new BowireRecording
        {
            Steps =
            {
                new BowireRecordingStep { Protocol = "rest" },
                new BowireRecordingStep { Protocol = "udp" }
            }
        };
        Assert.True(emitter.CanEmit(rec));
    }

    [Fact]
    public async Task CanEmit_FalseWhenRecordingHasNoUdpStep()
    {
        await using var emitter = new UdpMockEmitter();
        var rec = new BowireRecording
        {
            Steps = { new BowireRecordingStep { Protocol = "mqtt" } }
        };
        Assert.False(emitter.CanEmit(rec));
    }

    [Fact]
    public async Task EmitsPayloadOnUnicastDestination()
    {
        // Loopback unicast — no multicast join, just bind a receiver
        // on the same port the emitter sends to.
        var port = RandomPort();
        var payloadA = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var payloadB = Encoding.UTF8.GetBytes("hello from body");

        var recording = new BowireRecording
        {
            Id = "rec_udp",
            Name = "udp unicast round-trip",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "dgram_a",
                    Protocol = "udp",
                    CapturedAt = 0,
                    ResponseBinary = Convert.ToBase64String(payloadA),
                    Metadata = new Dictionary<string, string>
                    {
                        // Test both destination-URL and host/port path.
                        ["destination"] = $"udp://127.0.0.1:{port}"
                    }
                },
                new BowireRecordingStep
                {
                    Id = "dgram_b",
                    Protocol = "udp",
                    CapturedAt = 1,
                    // No responseBinary → emitter falls back to Body.
                    Body = "hello from body"
                }
            }
        };

        using var listener = new UdpClient(AddressFamily.InterNetwork);
        listener.ExclusiveAddressUse = false;
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));

        await using var emitter = new UdpMockEmitter();
        await emitter.StartAsync(
            recording,
            new MockEmitterOptions { ReplaySpeed = 0 }, // emit instantly
            NullLogger.Instance,
            CancellationToken.None);

        var received = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received.Count < 2 && !cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await listener.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            received.Add(result.Buffer);
        }

        Assert.Equal(2, received.Count);
        Assert.Contains(received, r => r.SequenceEqual(payloadA));
        Assert.Contains(received, r => r.SequenceEqual(payloadB));
    }

    [Fact]
    public async Task EmitsPayloadOnBroadcastDestination()
    {
        var port = RandomPort();
        var payload = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        var recording = new BowireRecording
        {
            Id = "rec_udp_bcast",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "bcast",
                    Protocol = "udp",
                    CapturedAt = 0,
                    ResponseBinary = Convert.ToBase64String(payload),
                    Metadata = new Dictionary<string, string>
                    {
                        ["destination"] = $"udp://255.255.255.255:{port}"
                    }
                }
            }
        };

        using var listener = new UdpClient(AddressFamily.InterNetwork);
        listener.ExclusiveAddressUse = false;
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));

        await using var emitter = new UdpMockEmitter();
        await emitter.StartAsync(
            recording,
            new MockEmitterOptions { ReplaySpeed = 0 },
            NullLogger.Instance,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await listener.ReceiveAsync(cts.Token);
        Assert.Equal(payload, result.Buffer);
    }

    [Fact]
    public void ReadDestination_DestinationUrl_OverridesHostPort()
    {
        var step = new BowireRecordingStep
        {
            Metadata = new Dictionary<string, string>
            {
                ["destination"] = "udp://239.1.2.3:4000",
                ["host"] = "1.1.1.1", // ignored because destination wins
                ["port"] = "9999"
            }
        };
        var (addr, port, mode, _) = UdpMockEmitter.ReadDestination(step);
        Assert.Equal(IPAddress.Parse("239.1.2.3"), addr);
        Assert.Equal(4000, port);
        Assert.Equal(UdpEndpoint.TransportMode.Multicast, mode);
    }

    [Fact]
    public void ReadDestination_HostPortFallback_WhenNoDestination()
    {
        var step = new BowireRecordingStep
        {
            Metadata = new Dictionary<string, string>
            {
                ["host"] = "127.0.0.1",
                ["port"] = "5514"
            }
        };
        var (addr, port, mode, _) = UdpMockEmitter.ReadDestination(step);
        Assert.Equal(IPAddress.Loopback, addr);
        Assert.Equal(5514, port);
        Assert.Equal(UdpEndpoint.TransportMode.Unicast, mode);
    }

    [Fact]
    public void ReadDestination_Defaults_WhenNoMetadata()
    {
        var step = new BowireRecordingStep();
        var (addr, port, mode, ttl) = UdpMockEmitter.ReadDestination(step);
        Assert.Equal(IPAddress.Broadcast, addr);
        Assert.Equal(UdpEndpoint.DefaultPort, port);
        Assert.Equal(UdpEndpoint.TransportMode.Broadcast, mode);
        Assert.Equal(1, ttl);
    }

    [Fact]
    public void DecodePayload_PrefersResponseBinary()
    {
        var step = new BowireRecordingStep
        {
            ResponseBinary = Convert.ToBase64String([0xDE, 0xAD]),
            Body = "ignored"
        };
        var bytes = UdpMockEmitter.DecodePayload(step, NullLogger.Instance);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, bytes);
    }

    [Fact]
    public void DecodePayload_FallsBackToBodyAsUtf8()
    {
        var step = new BowireRecordingStep { Body = "abc" };
        var bytes = UdpMockEmitter.DecodePayload(step, NullLogger.Instance);
        Assert.Equal(new byte[] { 0x61, 0x62, 0x63 }, bytes);
    }

    [Fact]
    public void DecodePayload_MalformedBase64_ReturnsNull()
    {
        var step = new BowireRecordingStep { ResponseBinary = "not-base64!" };
        Assert.Null(UdpMockEmitter.DecodePayload(step, NullLogger.Instance));
    }

    private static int RandomPort()
    {
#pragma warning disable CA5394
        return 43000 + Random.Shared.Next(0, 5000);
#pragma warning restore CA5394
    }
}
