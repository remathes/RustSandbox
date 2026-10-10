using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

// ---------------------------------------------------------------- wire format (sent by the plugin)

public sealed class TelemetryBatch
{
    public List<KillDto>? Kills { get; set; }
    public List<PositionDto>? Positions { get; set; }
    public List<ConnectionDto>? Connections { get; set; }
    public List<UsageDto>? Usage { get; set; }
    public List<GatherDto>? Gather { get; set; }
    public List<MonumentDto>? Monuments { get; set; }
}

/// <summary>A named monument at a world position (as labelled on the in-game map).</summary>
public sealed class MonumentDto
{
    public string? Name { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
}

public sealed class KillDto
{
    public string? KillerId { get; set; }
    public string? KillerName { get; set; }
    public string? KillerType { get; set; }
    public string? VictimId { get; set; }
    public string? VictimName { get; set; }
    public string? VictimType { get; set; }
    public string? Weapon { get; set; }
    public string? DamageType { get; set; }
    public string? BodyPart { get; set; }
    public bool Headshot { get; set; }
    public float Distance { get; set; }
    public float Vx { get; set; }
    public float Vz { get; set; }
    public float Kx { get; set; }
    public float Kz { get; set; }
    /// <summary>Unix milliseconds; server time is used when missing.</summary>
    public long? Time { get; set; }
}

public sealed class PositionDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
    public float Health { get; set; }
}

public sealed class ConnectionDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool Connected { get; set; }
}

public sealed class UsageDto
{
    public string? Id { get; set; }
    public string? Weapon { get; set; }
    public int Shots { get; set; }
    public int Hits { get; set; }
}

/// <summary>Resources a player gathered since the last report, per item and source.</summary>
public sealed class GatherDto
{
    public string? Id { get; set; }
    /// <summary>Item display name ("Metal Ore").</summary>
    public string? Item { get; set; }
    /// <summary>Item shortname ("metal.ore"), used to pick the resource group.</summary>
    public string? Code { get; set; }
    /// <summary>tree, ore, animal, pickup, farm or fishing.</summary>
    public string? Source { get; set; }
    public long Amount { get; set; }
    public int Times { get; set; }
}

/// <summary>Live positions and the latest kills per server, held in memory for the live map.</summary>
public sealed class LiveWorldStore
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, LivePlayer>> _players = new();
    private readonly ConcurrentDictionary<int, RingBuffer<KillEvent>> _kills = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastTelemetry = new();
    private readonly ConcurrentDictionary<int, List<MonumentDto>> _monuments = new();

    internal bool TryGetMonuments(int serverId, out List<MonumentDto>? list) => _monuments.TryGetValue(serverId, out list);
    internal void SetMonuments(int serverId, List<MonumentDto> list) => _monuments[serverId] = list;

    public IReadOnlyList<LivePlayer> Players(int serverId, TimeSpan? maxAge = null)
    {
        if (!_players.TryGetValue(serverId, out var map)) return Array.Empty<LivePlayer>();
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromSeconds(90));
        return map.Values.Where(p => p.Updated >= cutoff).OrderBy(p => p.Name).ToList();
    }

    public IReadOnlyList<KillEvent> RecentKills(int serverId, int count = 40) =>
        _kills.TryGetValue(serverId, out var buf) ? buf.Last(count) : Array.Empty<KillEvent>();

    public DateTime? LastTelemetryUtc(int serverId) => _lastTelemetry.TryGetValue(serverId, out var t) ? t : null;

    internal void Touch(int serverId) => _lastTelemetry[serverId] = DateTime.UtcNow;

    internal void Upsert(int serverId, LivePlayer p) =>
        _players.GetOrAdd(serverId, _ => new())[p.SteamId] = p;

    internal void Remove(int serverId, string steamId)
    {
        if (_players.TryGetValue(serverId, out var map)) map.TryRemove(steamId, out _);
    }

    internal void AddKill(int serverId, KillEvent k) => _kills.GetOrAdd(serverId, _ => new RingBuffer<KillEvent>(200)).Add(k);

    public void Clear(int serverId)
    {
        _players.TryRemove(serverId, out _);
        _kills.TryRemove(serverId, out _);
        _monuments.TryRemove(serverId, out _);
    }
}

/// <summary>Validates and stores what the in-game plugin reports.</summary>
public sealed class TelemetryService(
    IDbContextFactory<AppDbContext> dbFactory,
    LiveWorldStore world,
    LiveEvents live,
    DiscordNotifier discord,
    IConfiguration config,
    ILogger<TelemetryService> log)
{
    private readonly ConcurrentDictionary<int, (string Key, DateTime Loaded)> _keys = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastSample = new();

    private int SampleSeconds => Math.Max(5, config.GetValue("Manager:PositionSampleSeconds", 30));

    public async Task<bool> AuthorizeAsync(int serverId, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return false;
        if (!_keys.TryGetValue(serverId, out var cached) || DateTime.UtcNow - cached.Loaded > TimeSpan.FromMinutes(1))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var key = await db.Servers.Where(s => s.Id == serverId).Select(s => s.TelemetryApiKey).FirstOrDefaultAsync(ct);
            if (key is null) return false;
            cached = (key, DateTime.UtcNow);
            _keys[serverId] = cached;
        }
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(cached.Key), System.Text.Encoding.UTF8.GetBytes(apiKey));
    }

    public void InvalidateKey(int serverId) => _keys.TryRemove(serverId, out _);

    public async Task IngestAsync(int serverId, TelemetryBatch batch, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        world.Touch(serverId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ---- monuments (sent once per plugin load)
        var monuments = batch.Monuments?
            .Where(m => !string.IsNullOrWhiteSpace(m.Name) && float.IsFinite(m.X) && float.IsFinite(m.Z))
            .Select(m => new MonumentDto { Name = Trim(m.Name, 64), X = m.X, Z = m.Z })
            .ToList();
        if (monuments is { Count: > 0 })
        {
            var key = MonumentsKey(serverId);
            var json = System.Text.Json.JsonSerializer.Serialize(monuments);
            var row = await db.AppSettings.FindAsync(new object[] { key }, ct);
            if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = json });
            else row.Value = json;
            world.SetMonuments(serverId, monuments);
        }

        // ---- connections
        foreach (var c in batch.Connections ?? new())
        {
            if (string.IsNullOrEmpty(c.Id)) continue;
            db.Connections.Add(new ConnectionEvent { ServerId = serverId, SteamId = Trim(c.Id, 32), Name = Trim(c.Name, 64), IsConnect = c.Connected, Timestamp = now });
            if (!c.Connected) world.Remove(serverId, c.Id);
        }

        // ---- kills
        var newKills = new List<KillEvent>();
        foreach (var k in batch.Kills ?? new())
        {
            var e = new KillEvent
            {
                ServerId = serverId,
                Timestamp = k.Time is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(k.Time.Value).UtcDateTime : now,
                KillerId = Trim(k.KillerId, 32),
                KillerName = Trim(k.KillerName, 64),
                KillerType = ParseActor(k.KillerType),
                VictimId = Trim(k.VictimId, 32),
                VictimName = Trim(k.VictimName, 64),
                VictimType = ParseActor(k.VictimType),
                Weapon = Trim(k.Weapon, 64),
                DamageType = Trim(k.DamageType, 32),
                BodyPart = Trim(k.BodyPart, 32),
                IsHeadshot = k.Headshot,
                Distance = float.IsFinite(k.Distance) ? Math.Clamp(k.Distance, 0, 5000) : 0,
                VictimX = k.Vx, VictimZ = k.Vz, KillerX = k.Kx, KillerZ = k.Kz,
            };
            if (e.Timestamp > now.AddMinutes(5) || e.Timestamp < now.AddDays(-2)) e.Timestamp = now;
            newKills.Add(e);
        }
        db.Kills.AddRange(newKills);

        // ---- positions (live always, persisted on an interval for the heat map)
        if (batch.Positions is { Count: > 0 })
        {
            var persist = !_lastSample.TryGetValue(serverId, out var last) || (now - last).TotalSeconds >= SampleSeconds;
            if (persist) _lastSample[serverId] = now;
            foreach (var p in batch.Positions)
            {
                if (string.IsNullOrEmpty(p.Id) || !float.IsFinite(p.X) || !float.IsFinite(p.Z)) continue;
                world.Upsert(serverId, new LivePlayer { SteamId = p.Id, Name = p.Name ?? p.Id, X = p.X, Z = p.Z, Health = p.Health, Updated = now });
                if (persist) db.Positions.Add(new PositionSample { ServerId = serverId, SteamId = Trim(p.Id, 32), X = p.X, Z = p.Z, Timestamp = now });
            }
        }

        // ---- weapon usage (shots / hits), aggregated per day
        if (batch.Usage is { Count: > 0 })
        {
            var day = now.Date;
            var usage = batch.Usage
                .Where(u => !string.IsNullOrEmpty(u.Id) && !string.IsNullOrEmpty(u.Weapon))
                .GroupBy(u => (Id: Trim(u.Id, 32), Weapon: Trim(u.Weapon, 64)))
                .Select(g => (g.Key.Id, g.Key.Weapon, Shots: g.Sum(x => Math.Max(0, x.Shots)), Hits: g.Sum(x => Math.Max(0, x.Hits))))
                .ToList();
            var ids = usage.Select(u => u.Id).Distinct().ToList();
            var existing = await db.WeaponUsage
                .Where(w => w.ServerId == serverId && w.Day == day && ids.Contains(w.SteamId))
                .ToListAsync(ct);
            foreach (var u in usage)
            {
                var row = existing.FirstOrDefault(w => w.SteamId == u.Id && w.Weapon == u.Weapon);
                if (row is null)
                {
                    row = new WeaponUsage { ServerId = serverId, SteamId = u.Id, Weapon = u.Weapon, Day = day };
                    db.WeaponUsage.Add(row);
                    existing.Add(row);
                }
                row.Shots += u.Shots;
                row.Hits += u.Hits;
            }
        }

        // ---- gathering, aggregated per day
        if (batch.Gather is { Count: > 0 })
        {
            var day = now.Date;
            var gather = batch.Gather
                .Where(g => !string.IsNullOrEmpty(g.Id) && !string.IsNullOrEmpty(g.Code) && g.Amount > 0)
                .GroupBy(g => (Id: Trim(g.Id, 32), Item: Trim(string.IsNullOrEmpty(g.Item) ? g.Code : g.Item, 64), Source: Trim(g.Source, 16)))
                .Select(g => (g.Key.Id, g.Key.Item, g.Key.Source, Category: GatherCatalog.Category(g.First().Code),
                    Amount: g.Sum(x => Math.Min(x.Amount, 1_000_000)), Times: g.Sum(x => Math.Max(0, x.Times))))
                .ToList();
            var ids = gather.Select(g => g.Id).Distinct().ToList();
            var existing = await db.Gathering
                .Where(g => g.ServerId == serverId && g.Day == day && ids.Contains(g.SteamId))
                .ToListAsync(ct);
            foreach (var g in gather)
            {
                var row = existing.FirstOrDefault(r => r.SteamId == g.Id && r.Item == g.Item && r.Source == g.Source);
                if (row is null)
                {
                    row = new GatherStat { ServerId = serverId, SteamId = g.Id, Item = g.Item, Source = g.Source, Category = g.Category, Day = day };
                    db.Gathering.Add(row);
                    existing.Add(row);
                }
                row.Amount += g.Amount;
                row.Times += g.Times;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var k in newKills)
        {
            world.AddKill(serverId, k);
            live.RaiseKill(serverId, k);
            if (k.IsPvp) _ = discord.NotifyLongKillAsync(serverId, k);
        }
        if (batch.Positions is { Count: > 0 } || batch.Connections is { Count: > 0 }) live.RaisePositions(serverId);
        if (monuments is { Count: > 0 }) live.RaiseServersChanged(); // maps pick up the labels
        if (newKills.Count > 0) log.LogDebug("Server {Id}: stored {Count} kill events", serverId, newKills.Count);
    }

    public static string MonumentsKey(int serverId) => $"monuments:{serverId}";

    /// <summary>Real monument positions reported by the plugin (empty until it has reported once).</summary>
    public async Task<List<MonumentDto>> GetMonumentsAsync(int serverId)
    {
        if (world.TryGetMonuments(serverId, out var cached) && cached is not null) return cached;
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(a => a.Key == MonumentsKey(serverId));
        var list = row is null ? new List<MonumentDto>() :
            System.Text.Json.JsonSerializer.Deserialize<List<MonumentDto>>(row.Value) ?? new List<MonumentDto>();
        world.SetMonuments(serverId, list);
        return list;
    }

    private static string Trim(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    public static ActorType ParseActor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return ActorType.Unknown;
        if (Enum.TryParse<ActorType>(s, true, out var a)) return a;
        return s.ToLowerInvariant() switch
        {
            "npc" or "scientist" or "murderer" or "scarecrow" or "tunneldweller" or "underwaterdweller" => ActorType.Scientist,
            "heli" or "patrolhelicopter" or "attackhelicopter" or "chinook" => ActorType.Helicopter,
            "apc" or "bradleyapc" => ActorType.Bradley,
            "turret" or "autoturret" or "flameturret" or "samsite" or "guntrap" => ActorType.Turret,
            "landmine" or "beartrap" or "spikes" or "barricade" => ActorType.Trap,
            "suicide" => ActorType.Self,
            "fall" or "cold" or "heat" or "drowned" or "radiation" or "hunger" or "thirst" or "bleeding" or "poison" => ActorType.Environment,
            _ => ActorType.Unknown,
        };
    }
}
