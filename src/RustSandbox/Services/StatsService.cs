using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed record NamedValue(string Name, double Value, string? Detail = null);

public sealed class WeaponStat
{
    public string Weapon { get; init; } = "";
    public int Kills { get; set; }
    public int Headshots { get; set; }
    public double AvgDistance { get; set; }
    public double MaxDistance { get; set; }
    public long Shots { get; set; }
    public long Hits { get; set; }
    public double Accuracy => Shots > 0 ? (double)Hits / Shots : 0;
}

public sealed class PlayerRow
{
    public string SteamId { get; init; } = "";
    public string Name { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Headshots { get; set; }
    public int NpcKills { get; set; }
    public int AnimalKills { get; set; }
    public double Longest { get; set; }
    public string FavWeapon { get; set; } = "";
    public double KD => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);
    public double HeadshotRate => Kills == 0 ? 0 : (double)Headshots / Kills;
}

public sealed class CombatStats
{
    public int PvpKills { get; set; }
    public int PlayerDeaths { get; set; }
    public int Suicides { get; set; }
    public int NpcKills { get; set; }
    public int AnimalKills { get; set; }
    public int HeliKills { get; set; }
    public int BradleyKills { get; set; }
    public int Headshots { get; set; }
    public double HeadshotRate => PvpKills == 0 ? 0 : (double)Headshots / PvpKills;
    public double AvgPvpDistance { get; set; }
    public KillEvent? LongestKill { get; set; }
    public int UniquePlayers { get; set; }
    public long ShotsFired { get; set; }

    public List<WeaponStat> Weapons { get; set; } = new();
    public List<NamedValue> MostUsedWeapons { get; set; } = new();
    public List<NamedValue> DistanceBuckets { get; set; } = new();
    public List<NamedValue> VictimBreakdown { get; set; } = new();
    public List<NamedValue> DeathCauses { get; set; } = new();
    public List<NamedValue> BodyParts { get; set; } = new();
    public double[] KillsByHour { get; set; } = new double[24];
    public List<NamedValue> Timeline { get; set; } = new();
    public List<PlayerRow> Leaderboard { get; set; } = new();
    public List<KillEvent> Recent { get; set; } = new();
}

public sealed class PlayerStats
{
    public string SteamId { get; init; } = "";
    public string Name { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Suicides { get; set; }
    public int Headshots { get; set; }
    public int NpcKills { get; set; }
    public int AnimalKills { get; set; }
    public double KD => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);
    public double HeadshotRate => Kills == 0 ? 0 : (double)Headshots / Kills;
    public double AvgDistance { get; set; }
    public KillEvent? LongestKill { get; set; }
    public string FavWeapon { get; set; } = "";
    public string MostUsedWeapon { get; set; } = "";
    public long ShotsFired { get; set; }
    public double Accuracy { get; set; }
    public NamedValue? Nemesis { get; set; }
    public NamedValue? FavouriteVictim { get; set; }
    public List<NamedValue> KilledWhat { get; set; } = new();
    public List<NamedValue> KilledBy { get; set; } = new();
    public List<WeaponStat> Weapons { get; set; } = new();
    public List<NamedValue> DistanceBuckets { get; set; } = new();
    public List<KillEvent> Recent { get; set; } = new();
    public DateTime? FirstSeen { get; set; }
    public DateTime? LastSeen { get; set; }
    public int Sessions { get; set; }
    public long Gathered { get; set; }
    public string FavouriteResource { get; set; } = "";
    public List<NamedValue> GatheredByType { get; set; } = new();
    public List<NamedValue> GatheredItems { get; set; } = new();
}

public sealed class GathererRow
{
    public string SteamId { get; init; } = "";
    public string Name { get; set; } = "";
    public long Total { get; set; }
    public Dictionary<string, long> ByCategory { get; set; } = new();
    public string FavouriteItem { get; set; } = "";
    public long Get(string category) => ByCategory.GetValueOrDefault(category);
}

public sealed class GatherStats
{
    public long Total { get; set; }
    public long Times { get; set; }
    public int Gatherers { get; set; }
    /// <summary>Amount per resource type, in <see cref="GatherCatalog.Categories"/> order.</summary>
    public List<NamedValue> Categories { get; set; } = new();
    public List<NamedValue> TopItems { get; set; } = new();
    public List<NamedValue> Sources { get; set; } = new();
    public List<NamedValue> Timeline { get; set; } = new();
    public List<GathererRow> Leaderboard { get; set; } = new();
}

public sealed record PlayerSearchResult(string SteamId, string Name, int Kills, int Deaths);

/// <summary>Combat analytics built from the plugin's kill feed and weapon usage counters.</summary>
public sealed class StatsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public static readonly (string Label, float Min, float Max)[] Buckets =
    {
        ("0–10 m", 0, 10), ("10–25 m", 10, 25), ("25–50 m", 25, 50), ("50–100 m", 50, 100),
        ("100–200 m", 100, 200), ("200–300 m", 200, 300), ("300 m+", 300, float.MaxValue),
    };

    /// <summary>What a victim was, as shown in "what was killed" charts.</summary>
    public static string VictimLabel(KillEvent k) => k.VictimType switch
    {
        ActorType.Player => "Players",
        ActorType.Scientist => string.IsNullOrEmpty(k.VictimName) ? "Scientists" : k.VictimName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ? "Heavy Scientists" : "Scientists",
        ActorType.Animal => string.IsNullOrEmpty(k.VictimName) ? "Animals" : k.VictimName,
        ActorType.Helicopter => "Patrol Helicopter",
        ActorType.Bradley => "Bradley APC",
        _ => string.IsNullOrEmpty(k.VictimName) ? "Other" : k.VictimName,
    };

    /// <summary>What killed a player, grouped for "cause of death" charts.</summary>
    public static string CauseLabel(KillEvent k, bool namePlayers = false) => k.KillerType switch
    {
        ActorType.Player when k.KillerId == k.VictimId => "Suicide",
        ActorType.Player => namePlayers && !string.IsNullOrEmpty(k.KillerName) ? k.KillerName : "Players",
        ActorType.Self => "Suicide",
        ActorType.Scientist => "Scientists",
        ActorType.Animal => string.IsNullOrEmpty(k.KillerName) ? "Animals" : k.KillerName,
        ActorType.Helicopter => "Patrol Helicopter",
        ActorType.Bradley => "Bradley APC",
        ActorType.Turret or ActorType.Trap => string.IsNullOrEmpty(k.KillerName) ? "Traps & turrets" : k.KillerName,
        ActorType.Environment => string.IsNullOrEmpty(k.DamageType) ? "Environment" : k.DamageType,
        ActorType.Explosion => "Explosion",
        _ => string.IsNullOrEmpty(k.DamageType) ? (string.IsNullOrEmpty(k.KillerName) ? "Unknown" : k.KillerName) : k.DamageType,
    };

    private static List<NamedValue> Top(IEnumerable<string> keys, int take = 10) => keys
        .Where(k => !string.IsNullOrEmpty(k))
        .GroupBy(k => k)
        .Select(g => new NamedValue(g.Key, g.Count()))
        .OrderByDescending(v => v.Value)
        .Take(take)
        .ToList();

    private static List<NamedValue> DistanceHistogram(IEnumerable<KillEvent> kills)
    {
        var list = kills.ToList();
        return Buckets.Select(b => new NamedValue(b.Label, list.Count(k => k.Distance >= b.Min && k.Distance < b.Max))).ToList();
    }

    private async Task<List<KillEvent>> LoadKillsAsync(AppDbContext db, int serverId, DateTime? fromUtc, CancellationToken ct)
    {
        var q = db.Kills.AsNoTracking().Where(k => k.ServerId == serverId);
        if (fromUtc is not null) q = q.Where(k => k.Timestamp >= fromUtc.Value);
        return await q.OrderBy(k => k.Timestamp).ToListAsync(ct);
    }

    public async Task<CombatStats> GetServerStatsAsync(int serverId, DateTime? fromUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var kills = await LoadKillsAsync(db, serverId, fromUtc, ct);

        var usageQ = db.WeaponUsage.AsNoTracking().Where(w => w.ServerId == serverId);
        if (fromUtc is not null) usageQ = usageQ.Where(w => w.Day >= fromUtc.Value.Date);
        var usage = await usageQ.GroupBy(w => w.Weapon)
            .Select(g => new { Weapon = g.Key, Shots = g.Sum(x => (long)x.Shots), Hits = g.Sum(x => (long)x.Hits) })
            .ToListAsync(ct);

        var pvp = kills.Where(k => k.IsPvp).ToList();
        var byPlayers = kills.Where(k => k.KillerType == ActorType.Player).ToList();
        var stats = new CombatStats
        {
            PvpKills = pvp.Count,
            PlayerDeaths = kills.Count(k => k.IsPlayerDeath),
            Suicides = kills.Count(k => k.IsPlayerDeath && (k.KillerType == ActorType.Self || (k.KillerType == ActorType.Player && k.KillerId == k.VictimId))),
            NpcKills = byPlayers.Count(k => k.VictimType == ActorType.Scientist),
            AnimalKills = byPlayers.Count(k => k.VictimType == ActorType.Animal),
            HeliKills = byPlayers.Count(k => k.VictimType == ActorType.Helicopter),
            BradleyKills = byPlayers.Count(k => k.VictimType == ActorType.Bradley),
            Headshots = pvp.Count(k => k.IsHeadshot),
            AvgPvpDistance = pvp.Count > 0 ? pvp.Average(k => k.Distance) : 0,
            LongestKill = pvp.OrderByDescending(k => k.Distance).FirstOrDefault(),
            UniquePlayers = kills.Where(k => k.KillerType == ActorType.Player).Select(k => k.KillerId)
                .Concat(kills.Where(k => k.VictimType == ActorType.Player).Select(k => k.VictimId))
                .Where(id => id.Length > 0).Distinct().Count(),
            ShotsFired = usage.Sum(u => u.Shots),
            DistanceBuckets = DistanceHistogram(pvp),
            VictimBreakdown = Top(byPlayers.Where(k => !(k.VictimType == ActorType.Player && k.VictimId == k.KillerId)).Select(VictimLabel), 12),
            DeathCauses = Top(kills.Where(k => k.IsPlayerDeath).Select(k => CauseLabel(k)), 12),
            BodyParts = Top(pvp.Select(k => k.BodyPart), 8),
            Recent = kills.TakeLast(30).Reverse().ToList(),
        };

        var usageByWeapon = usage.ToDictionary(u => u.Weapon, u => u);
        stats.Weapons = byPlayers.Where(k => !string.IsNullOrEmpty(k.Weapon)).GroupBy(k => k.Weapon)
            .Select(g => new WeaponStat
            {
                Weapon = g.Key,
                Kills = g.Count(),
                Headshots = g.Count(k => k.IsHeadshot),
                AvgDistance = g.Average(k => k.Distance),
                MaxDistance = g.Max(k => k.Distance),
                Shots = usageByWeapon.TryGetValue(g.Key, out var u) ? u.Shots : 0,
                Hits = usageByWeapon.TryGetValue(g.Key, out var u2) ? u2.Hits : 0,
            })
            .OrderByDescending(w => w.Kills).ToList();
        stats.MostUsedWeapons = usage.OrderByDescending(u => u.Shots).Take(10)
            .Select(u => new NamedValue(u.Weapon, u.Shots, u.Shots > 0 ? $"{(double)u.Hits / u.Shots:P0} accuracy" : null)).ToList();

        var tz = TimeZoneInfo.Local;
        foreach (var k in pvp) stats.KillsByHour[TimeZoneInfo.ConvertTimeFromUtc(k.Timestamp, tz).Hour]++;

        // Timeline: hourly buckets for short ranges, daily otherwise.
        var span = DateTime.UtcNow - (fromUtc ?? (kills.Count > 0 ? kills[0].Timestamp : DateTime.UtcNow));
        var hourly = span <= TimeSpan.FromHours(48);
        stats.Timeline = kills.Where(k => k.IsPlayerDeath)
            .GroupBy(k =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(k.Timestamp, tz);
                return hourly ? new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0) : local.Date;
            })
            .OrderBy(g => g.Key)
            .Select(g => new NamedValue(hourly ? g.Key.ToString("ddd HH:00") : g.Key.ToString("MMM d"), g.Count(k => k.IsPvp), g.Count().ToString()))
            .ToList();

        stats.Leaderboard = BuildLeaderboard(kills);
        return stats;
    }

    private static List<PlayerRow> BuildLeaderboard(List<KillEvent> kills)
    {
        var rows = new Dictionary<string, PlayerRow>();
        PlayerRow Row(string id, string name)
        {
            if (!rows.TryGetValue(id, out var r)) rows[id] = r = new PlayerRow { SteamId = id, Name = name };
            if (!string.IsNullOrEmpty(name)) r.Name = name;
            return r;
        }
        var favs = new Dictionary<string, Dictionary<string, int>>();
        foreach (var k in kills)
        {
            if (k.KillerType == ActorType.Player && k.KillerId.Length > 0 && k.KillerId != k.VictimId)
            {
                var r = Row(k.KillerId, k.KillerName);
                switch (k.VictimType)
                {
                    case ActorType.Player:
                        r.Kills++;
                        if (k.IsHeadshot) r.Headshots++;
                        if (k.Distance > r.Longest) r.Longest = k.Distance;
                        if (!string.IsNullOrEmpty(k.Weapon))
                        {
                            if (!favs.TryGetValue(k.KillerId, out var f)) favs[k.KillerId] = f = new();
                            f[k.Weapon] = f.GetValueOrDefault(k.Weapon) + 1;
                        }
                        break;
                    case ActorType.Scientist: r.NpcKills++; break;
                    case ActorType.Animal: r.AnimalKills++; break;
                }
            }
            if (k.VictimType == ActorType.Player && k.VictimId.Length > 0) Row(k.VictimId, k.VictimName).Deaths++;
        }
        foreach (var (id, f) in favs) rows[id].FavWeapon = f.MaxBy(x => x.Value).Key;
        return rows.Values.OrderByDescending(r => r.Kills).ThenBy(r => r.Deaths).ToList();
    }

    public async Task<PlayerStats> GetPlayerStatsAsync(int serverId, string steamId, DateTime? fromUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Kills.AsNoTracking().Where(k => k.ServerId == serverId && (k.KillerId == steamId || k.VictimId == steamId));
        if (fromUtc is not null) q = q.Where(k => k.Timestamp >= fromUtc.Value);
        var events = await q.OrderBy(k => k.Timestamp).ToListAsync(ct);

        var usageQ = db.WeaponUsage.AsNoTracking().Where(w => w.ServerId == serverId && w.SteamId == steamId);
        if (fromUtc is not null) usageQ = usageQ.Where(w => w.Day >= fromUtc.Value.Date);
        var usage = await usageQ.GroupBy(w => w.Weapon)
            .Select(g => new { Weapon = g.Key, Shots = g.Sum(x => (long)x.Shots), Hits = g.Sum(x => (long)x.Hits) })
            .ToListAsync(ct);

        var gatherQ = db.Gathering.AsNoTracking().Where(g => g.ServerId == serverId && g.SteamId == steamId);
        if (fromUtc is not null) gatherQ = gatherQ.Where(g => g.Day >= fromUtc.Value.Date);
        var gathered = await gatherQ.GroupBy(g => new { g.Item, g.Category })
            .Select(g => new { g.Key.Item, g.Key.Category, Amount = g.Sum(x => x.Amount), Times = g.Sum(x => (long)x.Times) })
            .ToListAsync(ct);

        var connQ = db.Connections.AsNoTracking().Where(c => c.ServerId == serverId && c.SteamId == steamId);
        var firstSeen = await connQ.OrderBy(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(ct);
        var lastSeen = await connQ.OrderByDescending(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(ct);
        var sessions = await connQ.CountAsync(c => c.IsConnect, ct);

        var asKiller = events.Where(k => k.KillerId == steamId && k.KillerType == ActorType.Player && k.VictimId != steamId).ToList();
        var pvpKills = asKiller.Where(k => k.VictimType == ActorType.Player).ToList();
        var deaths = events.Where(k => k.VictimId == steamId && k.VictimType == ActorType.Player).ToList();

        var name = events.LastOrDefault(k => k.KillerId == steamId)?.KillerName
                   ?? events.LastOrDefault(k => k.VictimId == steamId)?.VictimName
                   ?? steamId;

        var s = new PlayerStats
        {
            SteamId = steamId,
            Name = name,
            Kills = pvpKills.Count,
            Deaths = deaths.Count,
            Suicides = deaths.Count(k => k.KillerType == ActorType.Self || k.KillerId == steamId),
            Headshots = pvpKills.Count(k => k.IsHeadshot),
            NpcKills = asKiller.Count(k => k.VictimType == ActorType.Scientist),
            AnimalKills = asKiller.Count(k => k.VictimType == ActorType.Animal),
            AvgDistance = pvpKills.Count > 0 ? pvpKills.Average(k => k.Distance) : 0,
            LongestKill = pvpKills.OrderByDescending(k => k.Distance).FirstOrDefault(),
            FavWeapon = pvpKills.Where(k => k.Weapon.Length > 0).GroupBy(k => k.Weapon).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "",
            MostUsedWeapon = usage.OrderByDescending(u => u.Shots).Select(u => u.Weapon).FirstOrDefault()
                             ?? asKiller.Where(k => k.Weapon.Length > 0).GroupBy(k => k.Weapon).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "",
            ShotsFired = usage.Sum(u => u.Shots),
            Accuracy = usage.Sum(u => u.Shots) > 0 ? (double)usage.Sum(u => u.Hits) / usage.Sum(u => u.Shots) : 0,
            KilledWhat = Top(asKiller.Select(k => k.VictimType == ActorType.Player ? k.VictimName : VictimLabel(k)), 12),
            KilledBy = Top(deaths.Select(k => CauseLabel(k, namePlayers: true)), 12),
            DistanceBuckets = DistanceHistogram(pvpKills),
            Recent = events.TakeLast(25).Reverse().ToList(),
            FirstSeen = firstSeen,
            LastSeen = lastSeen,
            Sessions = sessions,
            Gathered = gathered.Sum(g => g.Amount),
            FavouriteResource = gathered.OrderByDescending(g => g.Amount).Select(g => g.Item).FirstOrDefault() ?? "",
            GatheredByType = OrderedCategories(gathered.GroupBy(g => g.Category).Select(g => (g.Key, g.Sum(x => x.Amount)))),
            GatheredItems = gathered.OrderByDescending(g => g.Amount).Take(10)
                .Select(g => new NamedValue(g.Item, g.Amount, g.Times > 0 ? $"×{g.Times:N0}" : null)).ToList(),
        };
        if (s.Name == steamId)
            s.Name = await db.Connections.AsNoTracking().Where(c => c.ServerId == serverId && c.SteamId == steamId && c.Name != "")
                .OrderByDescending(c => c.Timestamp).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? steamId;

        var nemesis = deaths.Where(k => k.KillerType == ActorType.Player && k.KillerId != steamId)
            .GroupBy(k => k.KillerId).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (nemesis is not null) s.Nemesis = new NamedValue(nemesis.Last().KillerName, nemesis.Count(), nemesis.Key);
        var victim = pvpKills.GroupBy(k => k.VictimId).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (victim is not null) s.FavouriteVictim = new NamedValue(victim.Last().VictimName, victim.Count(), victim.Key);

        var usageByWeapon = usage.ToDictionary(u => u.Weapon, u => u);
        var weaponNames = asKiller.Select(k => k.Weapon).Concat(usage.Select(u => u.Weapon)).Where(w => w.Length > 0).Distinct();
        s.Weapons = weaponNames.Select(w =>
        {
            var wk = asKiller.Where(k => k.Weapon == w).ToList();
            var pk = wk.Where(k => k.VictimType == ActorType.Player).ToList();
            usageByWeapon.TryGetValue(w, out var u);
            return new WeaponStat
            {
                Weapon = w,
                Kills = wk.Count,
                Headshots = pk.Count(k => k.IsHeadshot),
                AvgDistance = wk.Count > 0 ? wk.Average(k => k.Distance) : 0,
                MaxDistance = wk.Count > 0 ? wk.Max(k => k.Distance) : 0,
                Shots = u?.Shots ?? 0,
                Hits = u?.Hits ?? 0,
            };
        }).OrderByDescending(w => w.Kills).ThenByDescending(w => w.Shots).ToList();

        return s;
    }

    private static List<NamedValue> OrderedCategories(IEnumerable<(string Category, long Amount)> totals)
    {
        var map = totals.GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        return GatherCatalog.Categories.Where(c => map.GetValueOrDefault(c) > 0).Select(c => new NamedValue(c, map[c])).ToList();
    }

    /// <summary>Gathering totals reported by the telemetry plugin (wood, ore, berries, mushrooms...).</summary>
    public async Task<GatherStats> GetGatherStatsAsync(int serverId, DateTime? fromUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Gathering.AsNoTracking().Where(g => g.ServerId == serverId);
        if (fromUtc is not null) q = q.Where(g => g.Day >= fromUtc.Value.Date);

        // One row per player, item and source over the range; small enough to slice in memory.
        var rows = await q.GroupBy(g => new { g.SteamId, g.Item, g.Category, g.Source })
            .Select(g => new { g.Key.SteamId, g.Key.Item, g.Key.Category, g.Key.Source, Amount = g.Sum(x => x.Amount), Times = g.Sum(x => (long)x.Times) })
            .ToListAsync(ct);
        var byDay = await q.GroupBy(g => g.Day).Select(g => new { Day = g.Key, Amount = g.Sum(x => x.Amount) }).OrderBy(g => g.Day).ToListAsync(ct);

        var ids = rows.Select(r => r.SteamId).Distinct().ToList();
        var names = await db.Connections.AsNoTracking()
            .Where(c => c.ServerId == serverId && ids.Contains(c.SteamId) && c.Name != "")
            .GroupBy(c => c.SteamId)
            .Select(g => new { Id = g.Key, Name = g.OrderByDescending(c => c.Timestamp).Select(c => c.Name).First() })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var missing = ids.Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var fromKills = await db.Kills.AsNoTracking()
                .Where(k => k.ServerId == serverId && missing.Contains(k.KillerId) && k.KillerName != "")
                .GroupBy(k => k.KillerId).Select(g => new { Id = g.Key, Name = g.Max(k => k.KillerName) })
                .ToListAsync(ct);
            foreach (var k in fromKills) names[k.Id] = k.Name;
        }

        return new GatherStats
        {
            Total = rows.Sum(r => r.Amount),
            Times = rows.Sum(r => r.Times),
            Gatherers = ids.Count,
            Categories = OrderedCategories(rows.Select(r => (r.Category, r.Amount))),
            TopItems = rows.GroupBy(r => r.Item)
                .Select(g => new NamedValue(g.Key, g.Sum(x => x.Amount), $"×{g.Sum(x => x.Times):N0}"))
                .OrderByDescending(v => v.Value).Take(15).ToList(),
            Sources = rows.GroupBy(r => r.Source)
                .Select(g => new NamedValue(GatherCatalog.SourceLabel(g.Key), g.Sum(x => x.Amount)))
                .OrderByDescending(v => v.Value).ToList(),
            Timeline = byDay.Select(d => new NamedValue(d.Day.ToString("MMM d"), d.Amount)).ToList(),
            Leaderboard = rows.GroupBy(r => r.SteamId)
                .Select(g => new GathererRow
                {
                    SteamId = g.Key,
                    Name = names.GetValueOrDefault(g.Key) ?? g.Key,
                    Total = g.Sum(x => x.Amount),
                    ByCategory = g.GroupBy(x => x.Category).ToDictionary(c => c.Key, c => c.Sum(x => x.Amount)),
                    FavouriteItem = g.GroupBy(x => x.Item).OrderByDescending(i => i.Sum(x => x.Amount)).First().Key,
                })
                .OrderByDescending(r => r.Total).ToList(),
        };
    }

    public async Task<List<PlayerSearchResult>> SearchPlayersAsync(int serverId, string? text, int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var killers = await db.Kills.Where(k => k.ServerId == serverId && k.KillerType == ActorType.Player && k.KillerId != "" && k.VictimType == ActorType.Player && k.KillerId != k.VictimId)
            .GroupBy(k => k.KillerId)
            .Select(g => new { Id = g.Key, Name = g.Max(x => x.KillerName), Count = g.Count() })
            .ToListAsync(ct);
        var victims = await db.Kills.Where(k => k.ServerId == serverId && k.VictimType == ActorType.Player && k.VictimId != "")
            .GroupBy(k => k.VictimId)
            .Select(g => new { Id = g.Key, Name = g.Max(x => x.VictimName), Count = g.Count() })
            .ToListAsync(ct);
        var map = new Dictionary<string, (string Name, int Kills, int Deaths)>();
        foreach (var k in killers) map[k.Id] = (k.Name ?? k.Id, k.Count, 0);
        foreach (var v in victims)
            map[v.Id] = map.TryGetValue(v.Id, out var e) ? (e.Name, e.Kills, v.Count) : (v.Name ?? v.Id, 0, v.Count);
        var rows = map.Select(kv => new PlayerSearchResult(kv.Key, kv.Value.Name, kv.Value.Kills, kv.Value.Deaths));
        IEnumerable<PlayerSearchResult> result = rows;
        if (!string.IsNullOrWhiteSpace(text))
            result = result.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.SteamId.Contains(text));
        return result.OrderByDescending(r => r.Kills + r.Deaths).Take(take).ToList();
    }
}
