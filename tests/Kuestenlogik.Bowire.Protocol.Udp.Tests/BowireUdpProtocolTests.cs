// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Udp.Tests;

public sealed class BowireUdpProtocolTests
{
    [Fact]
    public async Task Discover_WithValidUrl_ReturnsListenerService()
    {
        var plugin = new BowireUdpProtocol();
        var services = await plugin.DiscoverAsync("udp://127.0.0.1:5514", false, TestContext.Current.CancellationToken);
        Assert.Single(services);
        Assert.Equal(BowireUdpProtocol.ListenerServiceName, services[0].Name);
        var method = Assert.Single(services[0].Methods);
        Assert.Equal(BowireUdpProtocol.MonitorMethodName, method.Name);
        Assert.True(method.ServerStreaming);
    }

    [Fact]
    public async Task Discover_WithMalformedUrl_ReturnsEmpty()
    {
        var plugin = new BowireUdpProtocol();
        var services = await plugin.DiscoverAsync("http://example.com", false, TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_ReceiveOnlyMessage()
    {
        var plugin = new BowireUdpProtocol();
        var result = await plugin.InvokeAsync(
            "udp://127.0.0.1:5514", "Listener", "monitor", [], false, null, TestContext.Current.CancellationToken);
        Assert.Contains("receive-only", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeStream_YieldsEnvelopeForUnicastDatagram()
    {
        var port = RandomPort();

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var payload = Encoding.UTF8.GetBytes("hello udp");

        using var heartbeat = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeat.IsCancellationRequested)
            {
                try { await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port)); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                try { await Task.Delay(100, heartbeat.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            var plugin = new BowireUdpProtocol();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            string? envelope = null;
            await foreach (var msg in plugin.InvokeStreamAsync(
                $"udp://127.0.0.1:{port}",
                BowireUdpProtocol.ListenerServiceName,
                BowireUdpProtocol.MonitorMethodName,
                [],
                false,
                null,
                cts.Token))
            {
                envelope = msg;
                break;
            }

            Assert.NotNull(envelope);
            using var doc = JsonDocument.Parse(envelope!);
            Assert.Equal("hello udp", doc.RootElement.GetProperty("text").GetString());
            Assert.Equal(payload.Length, doc.RootElement.GetProperty("bytes").GetInt32());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("raw").GetString()));
        }
        finally
        {
            await heartbeat.CancelAsync();
            try { await heartbeatTask; } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task InvokeStream_YieldsEnvelopeForBroadcastDatagram()
    {
        var port = RandomPort();

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        sender.EnableBroadcast = true;
        var payload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF };

        using var heartbeat = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeat.IsCancellationRequested)
            {
                try { await sender.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, port)); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                try { await Task.Delay(100, heartbeat.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            var plugin = new BowireUdpProtocol();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            string? envelope = null;
            await foreach (var msg in plugin.InvokeStreamAsync(
                $"udp://255.255.255.255:{port}",
                BowireUdpProtocol.ListenerServiceName,
                BowireUdpProtocol.MonitorMethodName,
                [],
                false,
                null,
                cts.Token))
            {
                envelope = msg;
                break;
            }

            Assert.NotNull(envelope);
            using var doc = JsonDocument.Parse(envelope!);
            // Binary payload → text is JSON null because UTF-8 decode fails on 0xFF alone.
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("text").ValueKind);
            Assert.Equal(payload.Length, doc.RootElement.GetProperty("bytes").GetInt32());
        }
        finally
        {
            await heartbeat.CancelAsync();
            try { await heartbeatTask; } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void TryDecodeUtf8_InvalidBytes_ReturnsNull()
    {
        // 0xFF is not a valid UTF-8 lead byte on its own.
        Assert.Null(BowireUdpProtocol.TryDecodeUtf8([0xFF]));
    }

    [Fact]
    public void TryDecodeUtf8_ValidUtf8_ReturnsText()
    {
        var encoded = Encoding.UTF8.GetBytes("hallo — umlaute ok");
        Assert.Equal("hallo — umlaute ok", BowireUdpProtocol.TryDecodeUtf8(encoded));
    }

    private static int RandomPort()
    {
#pragma warning disable CA5394
        return 42000 + Random.Shared.Next(0, 5000);
#pragma warning restore CA5394
    }
}
