// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire;

// Two background emitters keep the UDP wire warm so the Bowire
// workbench has live datagrams to render the moment a user subscribes:
//
//   PositionPingEmitter ── 239.0.13.37:8137 ── multicast AIS-style position pings (~1.5 s)
//   PortCallEmitter     ── 127.0.0.1:8138    ── unicast port-call status lines  (~3.0 s)
//
// Browse:
//   1. dotnet run --project samples/Kuestenlogik.Bowire.Protocol.Udp.Sample
//   2. Open http://localhost:5080/bowire
//   3. Pick the "UDP" tab and subscribe to either
//        udp://239.0.13.37:8137   (multicast position pings)
//        udp://127.0.0.1:8138     (unicast port-call status)

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBowire();
builder.Services.AddHostedService<PositionPingEmitter>();
builder.Services.AddHostedService<PortCallEmitter>();

var app = builder.Build();
app.MapBowire();
app.Run();

/// <summary>Three real harbour vessels recycled across both emitters.</summary>
internal static class Fleet
{
    public static readonly Vessel[] Ships =
    [
        new(211_457_000, "Nordstern",  "Hamburg",  53.5413, 9.9786, "berth-A1"),
        new(219_018_500, "Isabella",   "Bremen",   53.0870, 8.7840, "berth-B3"),
        new(305_762_220, "Aurora",     "Cuxhaven", 53.8689, 8.7060, "berth-C2"),
        new(247_335_110, "Seestern",   "Wilhelmshaven", 53.5172, 8.1323, "berth-D4"),
    ];
}

internal sealed record Vessel(int Mmsi, string Name, string Port, double Lat, double Lon, string Berth);

/// <summary>
/// Multicasts an AIS-style position ping every ~1.5 s on
/// 239.0.13.37:8137. Loopback is enabled so a Bowire instance on the
/// same host sees its own packets.
/// </summary>
internal sealed class PositionPingEmitter(ILogger<PositionPingEmitter> logger) : BackgroundService
{
    public static readonly IPAddress Group = IPAddress.Parse("239.0.13.37");
    public const int Port = 8137;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // MulticastLoopback so a Bowire workbench on the same host
        // joining 239.0.13.37 also receives the packets we send.
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        var endpoint = new IPEndPoint(Group, Port);
        var rng = new Random();
        var index = 0;

        logger.LogInformation(
            "Multicast position-ping emitter starting → {Group}:{Port} (loopback ON, TTL 1)",
            Group, Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ship = Fleet.Ships[index % Fleet.Ships.Length];
            index++;

            // Drift each ship by a tiny random delta so successive
            // pings show movement, not constants.
            var lat = ship.Lat + ((rng.NextDouble() - 0.5) * 0.001);
            var lon = ship.Lon + ((rng.NextDouble() - 0.5) * 0.001);
            var sog = Math.Round(8.5 + (rng.NextDouble() * 4.0), 2); // knots
            var cog = Math.Round(rng.NextDouble() * 360.0, 1);       // degrees

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "position_ping",
                mmsi = ship.Mmsi,
                name = ship.Name,
                lat,
                lon,
                sog,
                cog,
                ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });

            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, endpoint, stoppingToken);
                logger.LogInformation(
                    "Multicast position-ping → {Group}:{Port}  mmsi={Mmsi} name={Name} lat={Lat:F4} lon={Lon:F4} sog={Sog} cog={Cog}",
                    Group, Port, ship.Mmsi, ship.Name, lat, lon, sog, cog);
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Multicast send failed (network adapter may be down)");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Multicast position-ping emitter stopped");
    }
}

/// <summary>
/// Unicasts a human-readable, pipe-delimited port-call status line every
/// ~3 s to 127.0.0.1:8138. Pipe-delimited so the UDP plugin's UTF-8
/// preview renders cleanly in the Bowire frame pane without JSON noise.
/// </summary>
internal sealed class PortCallEmitter(ILogger<PortCallEmitter> logger) : BackgroundService
{
    public static readonly IPAddress Host = IPAddress.Loopback;
    public const int Port = 8138;

    private static readonly string[] Statuses =
    [
        "ARRIVING", "MOORED", "UNLOADING", "LOADING", "CASTING_OFF", "DEPARTED",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = new IPEndPoint(Host, Port);
        var rng = new Random();
        var index = 0;

        logger.LogInformation(
            "Unicast port-call emitter starting → {Host}:{Port}", Host, Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ship = Fleet.Ships[index % Fleet.Ships.Length];
            var status = Statuses[index % Statuses.Length];
            var eta = DateTimeOffset.UtcNow.AddMinutes(rng.Next(5, 90));
            index++;

            // pipe-delimited line: easy to eyeball in a frame pane,
            // and any UDP-watching tool (nc -lu, Wireshark, Bowire)
            // shows the ASCII directly.
            var line = string.Create(CultureInfo.InvariantCulture,
                $"PORTCALL|mmsi={ship.Mmsi}|ship={ship.Name}|port={ship.Port}|berth={ship.Berth}|status={status}|eta={eta:O}");
            var payload = Encoding.UTF8.GetBytes(line);

            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, endpoint, stoppingToken);
                logger.LogInformation(
                    "Unicast port-call → {Host}:{Port}  ship={Name} berth={Berth} status={Status}",
                    Host, Port, ship.Name, ship.Berth, status);
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Unicast send failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Unicast port-call emitter stopped");
    }
}
