using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

public enum HeatLayer { Presence, PvpKills, Deaths, NpcKills }

/// <summary>A weighted point in world coordinates (x east, z north).</summary>
public sealed record HeatPoint(float X, float Z, float W);

/// <summary>Aggregates stored samples into weighted points the browser turns into a heat map.</summary>
public sealed class HeatmapService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Points are snapped to a grid this many metres wide before weighting.</summary>
    private const float Cell = 12f;
    private const int MaxPoints = 12_000;

    public async Task<List<HeatPoint>> GetAsync(int serverId, HeatLayer layer, DateTime? fromUtc, string? steamId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        List<(float X, float Z)> raw;
        switch (layer)
        {
            case HeatLayer.Presence:
            {
                var q = db.Positions.AsNoTracking().Where(p => p.ServerId == serverId);
                if (fromUtc is not null) q = q.Where(p => p.Timestamp >= fromUtc.Value);
                if (!string.IsNullOrEmpty(steamId)) q = q.Where(p => p.SteamId == steamId);
                raw = (await q.Select(p => new { p.X, p.Z }).ToListAsync(ct)).Select(p => (p.X, p.Z)).ToList();
                break;
            }
            default:
            {
                var q = db.Kills.AsNoTracking().Where(k => k.ServerId == serverId);
                if (fromUtc is not null) q = q.Where(k => k.Timestamp >= fromUtc.Value);
                q = layer switch
                {
                    HeatLayer.PvpKills => q.Where(k => k.KillerType == ActorType.Player && k.VictimType == ActorType.Player && k.KillerId != k.VictimId),
                    HeatLayer.Deaths => q.Where(k => k.VictimType == ActorType.Player),
                    _ => q.Where(k => k.KillerType == ActorType.Player && k.VictimType == ActorType.Scientist),
                };
                if (!string.IsNullOrEmpty(steamId)) q = q.Where(k => k.KillerId == steamId || k.VictimId == steamId);
                raw = (await q.Select(k => new { k.VictimX, k.VictimZ }).ToListAsync(ct)).Select(k => (k.VictimX, k.VictimZ)).ToList();
                break;
            }
        }

        var points = raw
            .GroupBy(p => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell)))
            .Select(g => new HeatPoint((g.Key.Item1 + 0.5f) * Cell, (g.Key.Item2 + 0.5f) * Cell, g.Count()))
            .OrderByDescending(p => p.W)
            .Take(MaxPoints)
            .ToList();
        return points;
    }
}
