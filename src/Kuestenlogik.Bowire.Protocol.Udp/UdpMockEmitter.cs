// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Udp;

/// <summary>
/// Plugs into Bowire's mock server via the
/// <see cref="IBowireMockEmitter"/> extension point. When a recording
/// contains steps tagged <c>protocol: "udp"</c>, the emitter opens a
/// UDP sender and re-broadcasts each step's captured payload on the
/// configured destination at the original cadence
/// (<see cref="BowireRecordingStep.CapturedAt"/>).
/// </summary>
/// <remarks>
/// <para>
/// Destination configuration — overridable via step metadata (first
/// UDP step's metadata wins):
/// </para>
/// <list type="bullet">
///   <item><c>destination</c>: full <c>udp://host:port</c> URL
///   (default <c>udp://255.255.255.255:3000</c>).</item>
///   <item><c>host</c> / <c>port</c>: alternative to <c>destination</c>,
///   set individually.</item>
///   <item><c>ttl</c>: multicast TTL (default <c>1</c>). Ignored for
///   broadcast / unicast transports.</item>
/// </list>
/// <para>
/// Transport mode (multicast join / SO_BROADCAST / plain unicast) is
/// inferred from the destination IP via
/// <see cref="UdpEndpoint.ClassifyAddress"/> — same classifier the
/// discovery side uses — so a captured multicast recording replays as
/// multicast, a broadcast recording as broadcast, and so on.
/// </para>
/// <para>
/// Payload source: <see cref="BowireRecordingStep.ResponseBinary"/>
/// (base64 of the raw datagram bytes) is used when present; otherwise
/// the emitter falls back to <see cref="BowireRecordingStep.Body"/>
/// encoded as UTF-8 so text-only recordings still replay. The runner
/// loops the sequence when <see cref="MockEmitterOptions.Loop"/> is set
/// and paces via <see cref="MockEmitterOptions.ReplaySpeed"/>.
/// </para>
/// </remarks>
public sealed class UdpMockEmitter : IBowireMockEmitter
{
    private UdpClient? _socket;
    private IPEndPoint? _destination;
    private CancellationTokenSource? _cts;
    private Task? _schedulerTask;
    private bool _disposed;

    /// <inheritdoc />
    public string Id => "udp";

    /// <inheritdoc />
    public bool CanEmit(BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return recording.Steps.Any(IsUdpStep);
    }

    /// <inheritdoc />
    public Task StartAsync(
        BowireRecording recording,
        MockEmitterOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var udpSteps = recording.Steps.Where(IsUdpStep).ToList();
        if (udpSteps.Count == 0) return Task.CompletedTask;

        var (address, port, mode, ttl) = ReadDestination(udpSteps[0]);
        _socket = new UdpClient(AddressFamily.InterNetwork);

        if (mode == UdpEndpoint.TransportMode.Multicast)
        {
            _socket.Client.SetSocketOption(
                SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
        }
        else if (mode == UdpEndpoint.TransportMode.Broadcast)
        {
            _socket.EnableBroadcast = true;
        }

        // Bind to an ephemeral port — the mock sends only.
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        _destination = new IPEndPoint(address, port);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _schedulerTask = Task.Run(() => RunAsync(udpSteps, options, logger, _cts.Token), _cts.Token);

        logger.LogInformation(
            "udp-emitter sending → udp://{Address}:{Port} (mode={Mode}, steps={Count})",
            address, port, mode, udpSteps.Count);
        return Task.CompletedTask;
    }

    private static bool IsUdpStep(BowireRecordingStep s) =>
        string.Equals(s.Protocol, "udp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the destination the emitter sends to. Priority:
    /// <c>destination</c> URL → <c>host</c> + <c>port</c> → defaults.
    /// </summary>
    internal static (IPAddress Address, int Port, UdpEndpoint.TransportMode Mode, int Ttl)
        ReadDestination(BowireRecordingStep first)
    {
        var metadata = first.Metadata;
        var address = IPAddress.Broadcast;
        var port = UdpEndpoint.DefaultPort;
        var ttl = 1;

        if (metadata is not null)
        {
            if (metadata.TryGetValue("destination", out var destStr))
            {
                var parsed = UdpEndpoint.TryParse(destStr);
                if (parsed is not null)
                {
                    return (parsed.Value.Address, parsed.Value.Port,
                            parsed.Value.Mode, ReadTtl(metadata, ttl));
                }
            }
            if (metadata.TryGetValue("host", out var hostStr) &&
                IPAddress.TryParse(hostStr, out var parsedHost))
            {
                address = parsedHost;
            }
            if (metadata.TryGetValue("port", out var portStr) &&
                int.TryParse(portStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
            {
                port = parsedPort;
            }
            ttl = ReadTtl(metadata, ttl);
        }

        var mode = UdpEndpoint.ClassifyAddress(address);
        return (address, port, mode, ttl);
    }

    private static int ReadTtl(IDictionary<string, string> metadata, int defaultTtl)
    {
        if (metadata.TryGetValue("ttl", out var ttlStr) &&
            int.TryParse(ttlStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return defaultTtl;
    }

    private async Task RunAsync(
        List<BowireRecordingStep> steps,
        MockEmitterOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        if (_socket is null || _destination is null) return;

        var baseCapturedAt = steps[0].CapturedAt;
        var speed = options.ReplaySpeed;

        do
        {
            var scheduleStartTicks = Environment.TickCount64;

            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();

                if (speed > 0)
                {
                    var targetOffsetMs = (long)((step.CapturedAt - baseCapturedAt) / speed);
                    var elapsed = Environment.TickCount64 - scheduleStartTicks;
                    var waitMs = targetOffsetMs - elapsed;
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                await EmitAsync(step, logger, ct);
            }
        }
        while (options.Loop && !ct.IsCancellationRequested);
    }

    private async Task EmitAsync(BowireRecordingStep step, ILogger logger, CancellationToken ct)
    {
        byte[]? payload = DecodePayload(step, logger);
        if (payload is null) return;

        try
        {
            await _socket!.SendAsync(payload, payload.Length, _destination);
            logger.LogInformation(
                "udp-emit(step={StepId}, bytes={Bytes})", step.Id, payload.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "udp-emitter send failed for step '{StepId}'; scheduler continues.", step.Id);
        }
    }

    /// <summary>
    /// Decode the step's payload to bytes. Prefers
    /// <see cref="BowireRecordingStep.ResponseBinary"/> (base64 raw
    /// bytes), falls back to UTF-8 encoding of
    /// <see cref="BowireRecordingStep.Body"/> so text-only recordings
    /// — such as syslog or human-readable telemetry — still replay.
    /// Returns <c>null</c> when neither is usable so the caller skips
    /// the step with a warning.
    /// </summary>
    internal static byte[]? DecodePayload(BowireRecordingStep step, ILogger logger)
    {
        if (!string.IsNullOrEmpty(step.ResponseBinary))
        {
            try
            {
                return Convert.FromBase64String(step.ResponseBinary);
            }
            catch (FormatException ex)
            {
                logger.LogWarning(
                    "udp-emitter skipping step '{StepId}': malformed base64 payload ({Message}).",
                    step.Id, ex.Message);
                return null;
            }
        }
        if (!string.IsNullOrEmpty(step.Body))
        {
            return System.Text.Encoding.UTF8.GetBytes(step.Body);
        }
        logger.LogWarning(
            "udp-emitter skipping step '{StepId}': neither responseBinary nor body present.",
            step.Id);
        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); }
            catch (ObjectDisposedException) { /* already torn down */ }
        }
        if (_schedulerTask is not null)
        {
            try { await _schedulerTask; }
            catch (OperationCanceledException) { /* expected */ }
            catch { /* scheduler cleanup is best-effort */ }
        }
        _socket?.Dispose();
        _cts?.Dispose();
    }
}
