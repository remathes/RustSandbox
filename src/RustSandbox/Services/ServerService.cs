using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>CRUD for server definitions, keeping the live connection layer in sync.</summary>
public sealed class ServerService(
    IDbContextFactory<AppDbContext> dbFactory,
    ServerConnectionManager conn,
    TelemetryService telemetry,
    LiveWorldStore world,
    LiveEvents live)
{
    public async Task<List<ServerInstance>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Servers.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync();
    }

    public async Task<ServerInstance?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ServerInstance> CreateAsync(ServerInstance s)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        s.SortOrder = (await db.Servers.MaxAsync(x => (int?)x.SortOrder) ?? 0) + 1;
        db.Servers.Add(s);
        await db.SaveChangesAsync();
        conn.UpdateServer(s);
        live.RaiseServersChanged();
        return s;
    }

    public async Task SaveAsync(ServerInstance s)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Servers.Update(s);
        await db.SaveChangesAsync();
        conn.UpdateServer(s);
        telemetry.InvalidateKey(s.Id);
        live.RaiseServersChanged();
        live.RaiseStatus(s.Id);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Kills.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.Positions.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.Connections.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.WeaponUsage.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.Gathering.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.Schedules.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.Macros.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        await db.Servers.Where(x => x.Id == id).ExecuteDeleteAsync();
        await conn.RemoveAsync(id);
        world.Clear(id);
        telemetry.InvalidateKey(id);
        live.RaiseServersChanged();
    }

    /// <summary>Delete recorded combat/heat-map history (e.g. at wipe). Returns rows removed.</summary>
    public async Task<int> ClearStatsAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.Kills.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        n += await db.Positions.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        n += await db.WeaponUsage.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        n += await db.Gathering.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        n += await db.Connections.Where(x => x.ServerId == id).ExecuteDeleteAsync();
        world.Clear(id);
        live.RaiseStatus(id);
        return n;
    }

    public async Task<string> RegenerateApiKeyAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.Servers.FirstAsync(x => x.Id == id);
        s.TelemetryApiKey = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        telemetry.InvalidateKey(id);
        return s.TelemetryApiKey;
    }

    public async Task ApplySeedAsync(int id, int seed, int worldSize)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.Servers.FirstAsync(x => x.Id == id);
        s.Settings.MapMode = MapMode.Procedural;
        s.Settings.Seed = seed;
        s.Settings.WorldSize = worldSize;
        await db.SaveChangesAsync();
        conn.UpdateServer(s);
        live.RaiseServersChanged();
    }

    // ---------------------------------------------------------------- app-wide settings

    public async Task<string?> GetAppSettingAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.AppSettings.FindAsync(key))?.Value;
    }

    public async Task SetAppSettingAsync(string key, string value)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.AppSettings.FindAsync(key);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
        if (key == "PublicUrl" && !string.IsNullOrWhiteSpace(value)) CustomMaps.DefaultHostUrl = value.Trim().TrimEnd('/');
    }
}
