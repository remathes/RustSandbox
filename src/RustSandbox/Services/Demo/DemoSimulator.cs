using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services.Demo;

/// <summary>Drives every demo server: movement, fights, chat and log chatter, plus a few days of back-filled history.</summary>
public sealed class DemoSimulator(
    DemoWorld world,
    TelemetryService telemetry,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<DemoSimulator> log) : BackgroundService
{
    private static readonly string[] ChatLines =
    {
        "anyone selling sulfur?", "outpost trade 1k scrap for an AK", "who's raiding at G12??", "gg", "heli is up north",
        "bradley at launch just got taken", "lol naked on the beach again", "need a duo, chill vibes", "cargo is moving in",
        "wipe was so good this month", "admin can you check D7 cheater?", "my base is the one with the purple door",
        "oil rig crate is up in 3", "whos got a mini for sale", "rip my loot",
    };

    private static readonly string[] LogLines =
    {
        "Saving complete", "[Oxide] Calling hook OnServerSave", "[event] assets/prefabs/npc/cargo plane/cargo_plane.prefab",
        "[event] assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab", "Invalid Position: generic (entity=npc) - Ignoring",
        "[Better Chat] Reloaded config", "Kicked: steamcommunity profile private", "Calling 'OnPlayerChat' on 'BetterChat' took 52ms",
    };

    /// <summary>What demo players gather: shortname, display name, source, amount per hit/pickup, relative frequency.</summary>
    private static readonly (string Code, string Item, string Source, int Min, int Max, int Weight)[] Gatherables =
    {
        ("wood", "Wood", "tree", 10, 40, 30), ("stones", "Stones", "ore", 15, 45, 20), ("metal.ore", "Metal Ore", "ore", 8, 30, 14),
        ("sulfur.ore", "Sulfur Ore", "ore", 6, 22, 10), ("hq.metal.ore", "High Quality Metal Ore", "ore", 1, 3, 3),
        ("cloth", "Cloth", "pickup", 10, 10, 8), ("mushroom", "Mushroom", "pickup", 1, 1, 6), ("red.berry", "Red Berry", "pickup", 1, 2, 3),
        ("blue.berry", "Blue Berry", "farm", 4, 8, 3), ("corn", "Corn", "farm", 2, 6, 3), ("pumpkin", "Pumpkin", "farm", 1, 3, 2),
        ("potato", "Potato", "farm", 2, 5, 2), ("leather", "Leather", "animal", 5, 20, 3), ("fat.animal", "Animal Fat", "animal", 5, 15, 3),
        ("bone.fragments", "Bone Fragments", "animal", 5, 20, 3), ("fish.raw", "Raw Fish", "fishing", 1, 1, 1),
    };

    private static (string Code, string Item, string Source, int Min, int Max, int Weight) PickGatherable(Random rng)
    {
        var roll = rng.Next(Gatherables.Sum(g => g.Weight));
        foreach (var g in Gatherables)
        {
            if (roll < g.Weight) return g;
            roll -= g.Weight;
        }
        return Gatherables[0];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment to finish starting.
        try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { return; }

        List<ServerInstance> demos = new();
        var lastLoad = DateTime.MinValue;
        var lastTick = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (DateTime.UtcNow - lastLoad > TimeSpan.FromSeconds(20))
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    demos = await db.Servers.AsNoTracking().Where(s => s.IsDemo).ToListAsync(stoppingToken);
                    lastLoad = DateTime.UtcNow;
                    foreach (var d in demos)
                    {
                        await BackfillAsync(d, stoppingToken);
                        await BackfillGatheringAsync(d, stoppingToken);
                    }
                }

                var now = DateTime.UtcNow;
                var dt = (now - lastTick).TotalSeconds;
                lastTick = now;
                foreach (var d in demos) await TickAsync(d, dt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Demo simulation tick failed"); }
        }
    }

    private async Task TickAsync(ServerInstance server, double seconds, CancellationToken ct)
    {
        var s = world.GetState(server);
        var batch = new TelemetryBatch { Kills = new(), Positions = new(), Connections = new(), Usage = new(), Gather = new() };
        string? chat = null, logLine = null;

        lock (s.Lock)
        {
            var rng = s.Rng;
            DemoWorld.Step(s, seconds);

            // Players come and go, with a busier evening.
            var hour = DateTime.Now.Hour;
            var targetOnline = (int)(s.Players.Count * (hour is >= 17 and <= 23 ? 0.75 : hour is >= 1 and <= 8 ? 0.35 : 0.55));
            var online = s.Players.Where(p => p.Online).ToList();
            if (rng.NextDouble() < 0.12)
            {
                if (online.Count < targetOnline)
                {
                    var p = s.Players.Where(x => !x.Online).OrderBy(_ => rng.Next()).FirstOrDefault();
                    if (p is not null)
                    {
                        p.Online = true; p.OnlineSinceUtc = DateTime.UtcNow; p.Health = 100;
                        batch.Connections.Add(new ConnectionDto { Id = p.SteamId, Name = p.Name, Connected = true });
                        logLine = $"{p.SteamId}/{p.Name} joined [windows/{p.SteamId}]";
                    }
                }
                else if (online.Count > 3)
                {
                    var p = online[rng.Next(online.Count)];
                    p.Online = false;
                    batch.Connections.Add(new ConnectionDto { Id = p.SteamId, Name = p.Name, Connected = false });
                    logLine = $"{p.SteamId}/{p.Name} disconnecting: disconnect";
                }
                online = s.Players.Where(p => p.Online).ToList();
            }

            foreach (var p in online)
                batch.Positions.Add(new PositionDto { Id = p.SteamId, Name = p.Name, X = p.X, Z = p.Z, Health = p.Health });

            if (online.Count >= 2)
            {
                var fights = rng.NextDouble() < 0.55 ? 1 : 0;
                if (rng.NextDouble() < 0.15) fights++;
                for (var i = 0; i < fights; i++)
                {
                    var k = world.GenerateKill(s, online, DateTime.UtcNow);
                    batch.Kills.Add(ToDto(k));
                    if (k.VictimType == ActorType.Player)
                    {
                        var victim = online.FirstOrDefault(p => p.SteamId == k.VictimId);
                        if (victim is not null)
                        {
                            // Respawn on a beach-ish spot.
                            var edge = s.WorldSize * 0.42f;
                            var a = rng.NextDouble() * Math.PI * 2;
                            victim.X = (float)Math.Cos(a) * edge; victim.Z = (float)Math.Sin(a) * edge;
                            victim.TargetX = victim.X; victim.TargetZ = victim.Z;
                            victim.Health = 60;
                        }
                    }
                }
                // Shots fired / hits
                foreach (var p in online.Where(_ => rng.NextDouble() < 0.25))
                {
                    var w = DemoWorld.PickWeapon(rng, p);
                    if (!w.Ranged) continue;
                    var shots = rng.Next(2, 30);
                    batch.Usage.Add(new UsageDto { Id = p.SteamId, Weapon = w.Name, Shots = shots, Hits = (int)(shots * (0.15 + p.Skill * 0.45) * rng.NextDouble() * 1.4) });
                }
            }

            // Gathering
            foreach (var p in online.Where(_ => rng.NextDouble() < 0.3))
            {
                var g = PickGatherable(rng);
                var hits = rng.Next(1, 6);
                batch.Gather.Add(new GatherDto { Id = p.SteamId, Code = g.Code, Item = g.Item, Source = g.Source, Amount = hits * rng.Next(g.Min, g.Max + 1), Times = hits });
            }

            if (rng.NextDouble() < 0.06 && online.Count > 0)
            {
                var p = online[rng.Next(online.Count)];
                chat = System.Text.Json.JsonSerializer.Serialize(new { Channel = rng.NextDouble() < 0.2 ? 1 : 0, Message = ChatLines[rng.Next(ChatLines.Length)], UserId = p.SteamId, Username = p.Name, Color = "#5af", Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            }
            if (logLine is null && rng.NextDouble() < 0.05) logLine = LogLines[rng.Next(LogLines.Length)];
            s.Entities = Math.Clamp(s.Entities + rng.Next(-40, 60), 120_000, 320_000);
        }

        if (chat is not null) world.Emit(server.Id, "Chat", chat);
        if (logLine is not null) world.Emit(server.Id, "Generic", logLine);
        await telemetry.IngestAsync(server.Id, batch, ct);
    }

    private static KillDto ToDto(KillEvent k) => new()
    {
        KillerId = k.KillerId, KillerName = k.KillerName, KillerType = k.KillerType.ToString(),
        VictimId = k.VictimId, VictimName = k.VictimName, VictimType = k.VictimType.ToString(),
        Weapon = k.Weapon, DamageType = k.DamageType, BodyPart = k.BodyPart, Headshot = k.IsHeadshot, Distance = k.Distance,
        Vx = k.VictimX, Vz = k.VictimZ, Kx = k.KillerX, Kz = k.KillerZ,
    };

    /// <summary>Three days of gathering for demo servers (separate from <see cref="BackfillAsync"/> so older demo databases get it too).</summary>
    private async Task BackfillGatheringAsync(ServerInstance server, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Gathering.AnyAsync(g => g.ServerId == server.Id, ct)) return;

        var s = world.GetState(server);
        var rows = new Dictionary<(string, string, string, DateTime), GatherStat>();
        lock (s.Lock)
        {
            var rng = s.Rng;
            for (var day = DateTime.UtcNow.Date.AddDays(-3); day <= DateTime.UtcNow.Date; day = day.AddDays(1))
            {
                foreach (var p in s.Players)
                {
                    // Some players farm a lot, some barely gather.
                    var sessions = rng.Next(0, 60);
                    for (var i = 0; i < sessions; i++)
                    {
                        var g = PickGatherable(rng);
                        var key = (p.SteamId, g.Item, g.Source, day);
                        if (!rows.TryGetValue(key, out var row))
                            rows[key] = row = new GatherStat { ServerId = server.Id, SteamId = p.SteamId, Item = g.Item, Category = GatherCatalog.Category(g.Code), Source = g.Source, Day = day };
                        var hits = rng.Next(3, 25);
                        row.Times += hits;
                        row.Amount += hits * rng.Next(g.Min, g.Max + 1);
                    }
                }
            }
        }
        db.Gathering.AddRange(rows.Values);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Fill a fresh demo server with ~3 days of history so every chart has something to show.</summary>
    private async Task BackfillAsync(ServerInstance server, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Kills.AnyAsync(k => k.ServerId == server.Id, ct)) return;

        log.LogInformation("Generating demo history for {Name}", server.Name);
        var s = world.GetState(server);
        var start = DateTime.UtcNow.AddDays(-3);
        var kills = new List<KillEvent>();
        var positions = new List<PositionSample>();
        var usage = new Dictionary<(string, string, DateTime), WeaponUsage>();
        var conns = new List<ConnectionEvent>();

        lock (s.Lock)
        {
            var rng = s.Rng;
            for (var t = start; t < DateTime.UtcNow; t = t.AddMinutes(2))
            {
                var hour = TimeZoneInfo.ConvertTimeFromUtc(t, TimeZoneInfo.Local).Hour;
                var activity = hour is >= 17 and <= 23 ? 1.0 : hour is >= 1 and <= 8 ? 0.3 : 0.6;
                var online = s.Players.Where((_, i) => (i * 37 + t.Hour * 11 + t.Day) % 100 < activity * 70).ToList();
                if (online.Count < 2) continue;
                DemoWorld.Step(s, 120, online);

                if (t.Minute % 10 == 0)
                    foreach (var p in online)
                        positions.Add(new PositionSample { ServerId = server.Id, SteamId = p.SteamId, X = p.X, Z = p.Z, Timestamp = t });

                var fights = (int)Math.Round(activity * (rng.NextDouble() * 3));
                for (var i = 0; i < fights; i++) kills.Add(world.GenerateKill(s, online, t.AddSeconds(rng.Next(0, 120))));

                foreach (var p in online.Where(_ => rng.NextDouble() < 0.3))
                {
                    var w = DemoWorld.PickWeapon(rng, p);
                    if (!w.Ranged) continue;
                    var key = (p.SteamId, w.Name, t.Date);
                    if (!usage.TryGetValue(key, out var u))
                        usage[key] = u = new WeaponUsage { ServerId = server.Id, SteamId = p.SteamId, Weapon = w.Name, Day = t.Date };
                    var shots = rng.Next(5, 60);
                    u.Shots += shots;
                    u.Hits += (int)(shots * (0.15 + p.Skill * 0.45));
                }

                if (t.Minute == 0)
                    foreach (var p in online.Take(3))
                        conns.Add(new ConnectionEvent { ServerId = server.Id, SteamId = p.SteamId, Name = p.Name, IsConnect = true, Timestamp = t });
            }
        }

        db.Kills.AddRange(kills);
        db.Positions.AddRange(positions);
        db.WeaponUsage.AddRange(usage.Values);
        db.Connections.AddRange(conns);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Demo history: {K} kills, {P} position samples", kills.Count, positions.Count);
    }
}
