// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Udp;

/// <summary>
/// Bowire protocol plugin for generic UDP listening. Given a
/// <c>udp://host:port</c> URL the plugin binds a socket and streams
/// every received datagram as a JSON envelope — useful for debugging
/// any UDP-based protocol (DIS, NetFlow, syslog, game-server
/// telemetry, custom sensor feeds) inside the Bowire workbench
/// without building a dedicated plugin for each.
/// </summary>
/// <remarks>
/// <para>
/// Address classification follows the same rules as the DIS plugin:
/// multicast (224.0.0.0/4) joins the group, broadcast
/// (255.255.255.255 or subnet-directed x.x.x.255) enables
/// <c>SO_BROADCAST</c>, and unicast simply binds to
/// <see cref="System.Net.IPAddress.Any"/> on the port. The keywords
/// <c>broadcast</c> and <c>multicast</c> are accepted as hostnames
/// for CLI ergonomics.
/// </para>
/// <para>
/// Each datagram surfaces as a JSON object with <c>source</c>
/// (IP:port the packet came from), <c>bytes</c>, <c>raw</c> (base64),
/// and — when the payload decodes as valid UTF-8 — <c>text</c>. The
/// workbench's stream-output view takes it from there.
/// </para>
/// </remarks>
public sealed class BowireUdpProtocol : IBowireProtocol
{
    /// <summary>Synthetic service name for the UDP listener.</summary>
    public const string ListenerServiceName = "Listener";

    /// <summary>Method name that streams the datagram feed.</summary>
    public const string MonitorMethodName = "monitor";

    /// <inheritdoc />
    public string Name => "UDP";

    /// <inheritdoc />
    public string Id => "udp";

    /// <inheritdoc />
    // Generic "arrow into network" glyph. UDP has no logo; this
    // matches the broadcast-style icons used by MQTT and DIS.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="#fbbf24" stroke-width="1.5" width="16" height="16" aria-hidden="true"><path d="M3 12h13m0 0l-4-4m4 4l-4 4"/><circle cx="20" cy="12" r="1.5"/></svg>""";

    /// <inheritdoc />
    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var endpoint = UdpEndpoint.TryParse(serverUrl);
        if (endpoint is null) return Task.FromResult(new List<BowireServiceInfo>());

        var description =
            $"UDP {endpoint.Value.Mode} listener on " +
            $"{endpoint.Value.Address}:{endpoint.Value.Port}. Every datagram arrives as a JSON envelope.";

        var services = new List<BowireServiceInfo>
        {
            new(ListenerServiceName, "udp", [BuildMonitorMethod(description)])
            {
                Source = "udp",
                OriginUrl = serverUrl,
                Description = description,
            },
        };
        return Task.FromResult(services);
    }

    /// <inheritdoc />
    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // UDP listening is receive-only — there is no unary invocation.
        return Task.FromResult(new InvokeResult(
            null, 0,
            "UDP listener is receive-only. Open the monitor stream to observe incoming datagrams.",
            new Dictionary<string, string>()));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoint = UdpEndpoint.TryParse(serverUrl);
        if (endpoint is null) yield break;

        using var socket = UdpEndpoint.CreateListenSocket(endpoint.Value, out var joinedGroup);
        if (socket is null) yield break;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await socket.ReceiveAsync(ct); }
                catch (OperationCanceledException) { yield break; }
                catch (SocketException) { yield break; }

                yield return BuildEnvelope(result);
            }
        }
        finally
        {
            if (joinedGroup) try { socket.DropMulticastGroup(endpoint.Value.Address); } catch { /* best-effort */ }
        }
    }

    /// <inheritdoc />
    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    private static BowireMethodInfo BuildMonitorMethod(string description) =>
        new(
            Name: MonitorMethodName,
            FullName: $"udp/{ListenerServiceName}/{MonitorMethodName}",
            ClientStreaming: false,
            ServerStreaming: true,
            InputType: new BowireMessageInfo("UdpMonitorRequest", "udp.MonitorRequest", []),
            OutputType: BuildMonitorOutput(),
            MethodType: "ServerStreaming")
        {
            Summary = "Live UDP datagram feed",
            Description = description,
        };

    private static BowireMessageInfo BuildMonitorOutput() => new(
        "UdpDatagram", "udp.Datagram",
        [
            new BowireFieldInfo("source", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Remote endpoint the datagram came from (ip:port).",
            },
            new BowireFieldInfo("bytes", 2, "int32", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("text", 3, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "UTF-8 decoded payload when the datagram is valid UTF-8; absent otherwise.",
            },
            new BowireFieldInfo("raw", 4, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Base64 of the full datagram bytes.",
            },
        ]);

    internal static string BuildEnvelope(UdpReceiveResult result)
    {
        var bytes = result.Buffer;
        string? text = TryDecodeUtf8(bytes);
        var envelope = new
        {
            source = result.RemoteEndPoint.ToString(),
            bytes = bytes.Length,
            text,
            raw = Convert.ToBase64String(bytes),
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Attempt a strict UTF-8 decode. Returns the string when every
    /// byte maps; null when any invalid byte appears (binary data).
    /// The stream-output UI shows <c>text</c> preferentially; callers
    /// can still hex-dump <c>raw</c>.
    /// </summary>
    internal static string? TryDecodeUtf8(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
