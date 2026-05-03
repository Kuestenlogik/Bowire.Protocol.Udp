# Sample — UDP harbour broadcast

A small ASP.NET Core host that emits two flavours of harbour-domain UDP
traffic side by side, so the Bowire workbench's **UDP** tab has live
datagrams to render the moment a user subscribes:

| Hosted service        | Endpoint                | Cadence | Payload                              |
|-----------------------|-------------------------|---------|--------------------------------------|
| `PositionPingEmitter` | `udp://239.0.13.37:8137`| ~1.5 s  | AIS-style JSON position ping         |
| `PortCallEmitter`     | `udp://127.0.0.1:8138`  | ~3.0 s  | pipe-delimited port-call status line |

Both emitters cycle through four real-world North-Sea vessels
(`Nordstern`, `Isabella`, `Aurora`, `Seestern`) with small random
position drift, so the live stream never goes quiet and never repeats
verbatim.

## Run

```sh
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.Udp.Sample
```

You'll see one log line per emitted datagram — that confirms the wire
is hot even before any Bowire viewer has attached.

## Watch in Bowire

1. Open <http://localhost:5080/bowire> (or run the standalone `bowire`
   CLI).
2. Pick the **UDP** tab.
3. Subscribe to either:
   - `udp://239.0.13.37:8137` — multicast position pings
   - `udp://127.0.0.1:8138`   — unicast port-call status

Live datagrams arrive in the frame pane as JSON envelopes (source
`IP:port`, byte count, base64 raw, UTF-8 preview).

## Multicast group + port choices

- **`239.0.13.37`** lives in the IANA **administratively-scoped**
  range (`239.0.0.0/8`), reserved for site-/org-local use — never
  routed to the public internet.
- **`8137` / `8138`** are unprivileged ports picked to be memorable
  (`8137 = 8000 + harbour-mnemonic`) and not collide with the usual
  syslog / DNS / mDNS suspects.
- TTL is set to **1** on the multicast socket so emissions stay on the
  local link. Adjust `MulticastTimeToLive` in
  `PositionPingEmitter.ExecuteAsync` if you need them to escape the
  subnet.

## Windows firewall

On the first run Windows may prompt you to allow the host process to
accept incoming connections. Tick **"Private networks"** and click
**Allow access** — without that, multicast loopback to a Bowire
instance running on the same machine may be silently dropped.

## What you'll see in the frame pane

A multicast position-ping renders as:

```json
{
  "source": "192.168.1.42:53812",
  "bytes": 187,
  "text": "{\"type\":\"position_ping\",\"mmsi\":211457000,\"name\":\"Nordstern\", … }",
  "raw": "eyJ0eXBlIjoicG9zaXRpb25fcGluZyIs…"
}
```

A unicast port-call status renders as:

```json
{
  "source": "127.0.0.1:54219",
  "bytes": 96,
  "text": "PORTCALL|mmsi=219018500|ship=Isabella|port=Bremen|berth=B3|status=MOORED|eta=…",
  "raw": "UE9SVENBTEx8…"
}
```

The plugin keeps `raw` (base64) on every datagram so the workbench can
fall back to a hex dump if anything ever sends non-UTF-8 bytes — but
this sample stays human-readable on purpose.
