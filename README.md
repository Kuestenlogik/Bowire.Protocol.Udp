# Bowire.Protocol.Udp

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire.Protocol.Udp/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire.Protocol.Udp/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/Kuestenlogik/Bowire.Protocol.Udp/branch/main/graph/badge.svg)](https://codecov.io/gh/Kuestenlogik/Bowire.Protocol.Udp)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire.Protocol.Udp)](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.Udp)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire.Protocol.Udp)](https://github.com/Kuestenlogik/Bowire.Protocol.Udp/blob/main/LICENSE)

Generic UDP listener plugin for the [Bowire](https://github.com/Kuestenlogik/Bowire) workbench.

Bind to any UDP endpoint — multicast group, limited or subnet-directed broadcast, or unicast address — and stream every received datagram into the workbench as a JSON envelope. Useful for debugging any UDP-based protocol without a dedicated plugin: DIS (when you want raw bytes alongside `Bowire.Protocol.Dis`'s typed decoding), NetFlow, syslog, game-server telemetry, custom sensor feeds, …

## URL shapes

```
udp://127.0.0.1:5514          # unicast — listen on loopback port 5514
udp://239.255.0.1:3000        # multicast — joins the group
udp://255.255.255.255:3000    # limited broadcast
udp://192.168.1.255:3000      # subnet-directed broadcast
udp://broadcast:3000          # shortcut for 255.255.255.255
udp://multicast:3000          # shortcut for 239.1.2.3
```

The transport mode is inferred from the IP address — multicast (`224.0.0.0/4`) joins the group, broadcast (`255.255.255.255` or any address ending in `.255`) enables `SO_BROADCAST`, everything else binds as unicast.

## Envelope

Each datagram arrives as:

```json
{
  "source": "10.0.0.5:53812",
  "bytes": 144,
  "text": "optional UTF-8 decoded string when the payload is valid UTF-8",
  "raw": "<base64 of the full datagram>"
}
```

`text` is absent (JSON `null`) when the payload contains non-UTF-8 bytes; the raw base64 is always present so the workbench can hex-dump it regardless.

## Mock replay

The plugin contributes a `UdpMockEmitter` that plugs into the `bowire mock` server. Any recording with steps tagged `protocol: "udp"` gets re-broadcast on UDP at the original cadence (from `capturedAt`), honouring `MockEmitterOptions.Loop` / `.ReplaySpeed`.

Destination is read from the first UDP step's metadata, with sensible defaults:

| Metadata key | Purpose | Default |
|--------------|---------|---------|
| `destination` | Full `udp://host:port` URL (overrides `host`/`port`) | `udp://255.255.255.255:3000` |
| `host` / `port` | Alternative, set individually | `255.255.255.255` / `3000` |
| `ttl` | Multicast TTL; ignored for broadcast / unicast | `1` |

Transport mode (multicast join / `SO_BROADCAST` / plain unicast) is inferred from the destination IP, same classifier the discovery side uses — a multicast recording replays as multicast, a broadcast recording as broadcast, and so on.

Payload source: `responseBinary` (base64 raw bytes) is preferred; otherwise the emitter falls back to `body` encoded as UTF-8, so text-only recordings (syslog, human-readable telemetry, …) still replay.

## Relationship to `Bowire.Protocol.Dis`

`Bowire.Protocol.Dis` gives you typed DIS decoding (entity discovery, PDU filtering, typed envelopes per IEEE 1278.1). `Bowire.Protocol.Udp` is the low-level cousin — it doesn't decode any protocol, it just surfaces bytes. Run both at once: DIS on one URL for typed per-entity streams, UDP on the same port for the raw-bytes view. Both plugins ship a mock emitter so recordings captured via either can be replayed; pick the protocol string (`dis` vs `udp`) on your steps to route them to the right emitter.

## Sample

A runnable end-to-end sample lives under [`samples/Kuestenlogik.Bowire.Protocol.Udp.Sample`](samples/Kuestenlogik.Bowire.Protocol.Udp.Sample) — an ASP.NET Core host that emits AIS-style position pings on multicast `239.0.13.37:8137` (~1.5 s cadence) and pipe-delimited port-call status lines on unicast `127.0.0.1:8138` (~3 s cadence), so the **UDP** tab has live datagrams to render the moment you subscribe.

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.Udp.Sample
```

## Install

```
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire plugin install Kuestenlogik.Bowire.Protocol.Udp
```

Then start the workbench:

```
bowire
```

and enter a UDP URL in the sidebar.
