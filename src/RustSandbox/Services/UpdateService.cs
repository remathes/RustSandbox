using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed class UpdateStatus
{
    public string? InstalledBuild { get; internal set; }
    public string? LatestBuild { get; internal set; }
    public string? FrameworkInstalled { get; internal set; }
    public string? FrameworkLatest { get; internal set; }
    public DateTime? CheckedUtc { get; internal set; }
    public string? Error { get; internal set; }

    public bool RustUpdate => InstalledBuild is not null && LatestBuild is not null && InstalledBuild != LatestBuild
                              && long.TryParse(LatestBuild, out var l) && long.TryParse(InstalledBuild, out var i) && l > i;
    public bool FrameworkUpdate => UpdateService.IsNewer(FrameworkLatest, FrameworkInstalled);
    public bool AnyUpdate => RustUpdate || FrameworkUpdate;

    internal string? NotifiedKey { get; set; }
    internal DateTime? AutoUpdateStartedUtc { get; set; }
}

/// <summary>
/// Checks every 30 minutes whether Rust (Steam build) or Oxide/Carbon have newer versions,
/// tells people (dashboard badge, console, Discord) and optionally applies updates safely.
/// </summary>
public sealed partial class UpdateService(
    IDbContextFactory<AppDbContext> dbFactory,
    ServerConnectionManager conn,
    ProcessManager processes,
    DiscordNotifier discord,
    LiveEvents live,
    IHttpClientFactory http,
    ILogger<UpdateService> log) : BackgroundService
{
    private readonly ConcurrentDictionary<int, UpdateStatus> _status = new();
    private readonly ConcurrentDictionary<string, (string? Value, DateTime At)> _cache = new();

    public UpdateStatus Get(int serverId) => _status.GetOrAdd(serverId, _ => new UpdateStatus());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var servers = await db.Servers.AsNoTracking().Where(s => !s.IsDemo).ToListAsync(stoppingToken);
                foreach (var s in servers)
                {
                    await CheckAsync(s, stoppingToken);
                    await MaybeAutoUpdateAsync(s);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Update check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task MaybeAutoUpdateAsync(ServerInstance s)
    {
        var st = Get(s.Id);
        if (!s.Settings.AutoUpdate || !s.Settings.ManageProcess || !st.AnyUpdate) return;
        if (processes.Get(s.Id).Activity is not null) return;
        if (st.AutoUpdateStartedUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromHours(2)) return; // don't loop on a failing update
        st.AutoUpdateStartedUtc = DateTime.UtcNow;
        conn.AppendConsole(s.Id, ConsoleKind.System, "Auto-update: a new version is available, updating now.");
        var err = await UpdateNowAsync(s, s.Settings.UpdateWarnMinutes);
        if (err is not null) conn.AppendConsole(s.Id, ConsoleKind.Error, $"Auto-update failed: {err}");
    }

    public async Task<string?> UpdateNowAsync(ServerInstance s, int warnMinutes)
    {
        var err = await processes.UpdateAndRestartAsync(s, warnMinutes);
        await CheckAsync(s);
        return err;
    }

    public async Task CheckAsync(ServerInstance s, CancellationToken ct = default)
    {
        if (s.IsDemo)
        {
            var demo = Get(s.Id);
            demo.InstalledBuild = demo.LatestBuild = "20140123";
            demo.FrameworkInstalled = demo.FrameworkLatest = "2.0.6400";
            demo.CheckedUtc = DateTime.UtcNow;
            live.RaiseStatus(s.Id);
            return;
        }

        var st = Get(s.Id);
        var errors = new List<string>();
        var local = s.Settings.ManageProcess && Directory.Exists(s.Settings.InstallDir);

        // ---- Rust build
        st.InstalledBuild = local ? ReadInstalledBuild(s.Settings.InstallDir) : null;
        try { st.LatestBuild = await LatestRustBuildAsync(s.Settings.Branch, ct); }
        catch (Exception ex) { errors.Add("Steam: " + ex.Message); }

        // ---- framework
        st.FrameworkInstalled = null;
        st.FrameworkLatest = null;
        if (s.Settings.Framework != ModFramework.Vanilla)
        {
            st.FrameworkInstalled = local ? ReadFrameworkVersion(s.Settings) : null;
            if (st.FrameworkInstalled is null) st.FrameworkInstalled = await FrameworkVersionOverRconAsync(s);
            try { st.FrameworkLatest = await LatestFrameworkAsync(s.Settings.Framework, ct); }
            catch (Exception ex) { errors.Add("GitHub: " + ex.Message); }
        }

        st.CheckedUtc = DateTime.UtcNow;
        st.Error = errors.Count == 0 ? null : string.Join("; ", errors);

        if (st.AnyUpdate)
        {
            var key = $"{st.LatestBuild}|{st.FrameworkLatest}";
            if (st.NotifiedKey != key)
            {
                st.NotifiedKey = key;
                var parts = new List<string>();
                if (st.RustUpdate) parts.Add($"Rust build {st.InstalledBuild} → {st.LatestBuild}");
                if (st.FrameworkUpdate) parts.Add($"{s.Settings.Framework} {st.FrameworkInstalled} → {st.FrameworkLatest}");
                var text = string.Join(", ", parts);
                conn.AppendConsole(s.Id, ConsoleKind.Warning, $"Update available: {text}. Use “Update now” on the dashboard.");
                _ = discord.NotifyAsync(s.Id, "⬆️ Update available", $"{s.Settings.Hostname}: {text}", DiscordNotifier.Amber, isStatus: true);
            }
        }
        live.RaiseStatus(s.Id);
    }

    // ---------------------------------------------------------------- installed versions

    public static string? ReadInstalledBuild(string installDir)
    {
        var manifest = Path.Combine(installDir, "steamapps", "appmanifest_258550.acf");
        if (!File.Exists(manifest)) return null;
        var m = BuildIdRegex().Match(File.ReadAllText(manifest));
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ReadFrameworkVersion(ServerSettings s)
    {
        var dll = s.Framework switch
        {
            ModFramework.Oxide => Path.Combine(s.InstallDir, "RustDedicated_Data", "Managed", "Oxide.Rust.dll"),
            ModFramework.Carbon => Path.Combine(s.InstallDir, "carbon", "managed", "Carbon.dll"),
            _ => null,
        };
        if (dll is null || !File.Exists(dll)) return null;
        var info = FileVersionInfo.GetVersionInfo(dll);
        var v = VersionRegex().Match(info.ProductVersion ?? info.FileVersion ?? "");
        return v.Success ? v.Value : info.FileVersion;
    }

    private async Task<string?> FrameworkVersionOverRconAsync(ServerInstance s)
    {
        var cmd = s.Settings.Framework == ModFramework.Carbon ? "c.version" : "oxide.version";
        var reply = await conn.QueryAsync(s.Id, cmd, TimeSpan.FromSeconds(5));
        if (string.IsNullOrEmpty(reply)) return null;
        var m = VersionRegex().Match(reply);
        return m.Success ? m.Value : null;
    }

    // ---------------------------------------------------------------- latest versions (cached 10 min)

    private async Task<string?> Cached(string key, Func<Task<string?>> fetch)
    {
        if (_cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10)) return c.Value;
        var value = await fetch();
        _cache[key] = (value, DateTime.UtcNow);
        return value;
    }

    /// <summary>Latest Steam build id for Rust Dedicated Server (app 258550) on a branch.</summary>
    private Task<string?> LatestRustBuildAsync(string? branch, CancellationToken ct) => Cached("rust:" + branch, async () =>
    {
        var name = string.IsNullOrWhiteSpace(branch) ? "public" : branch.Trim();
        var client = http.CreateClient("downloads");
        using var doc = JsonDocument.Parse(await client.GetStringAsync("https://api.steamcmd.net/v1/info/258550", ct));
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("258550", out var app) &&
            app.TryGetProperty("depots", out var depots) &&
            depots.TryGetProperty("branches", out var branches) &&
            branches.TryGetProperty(name, out var b) &&
            b.TryGetProperty("buildid", out var id))
            return id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
        throw new InvalidOperationException($"branch '{name}' not found");
    });

    private Task<string?> LatestFrameworkAsync(ModFramework fw, CancellationToken ct) => Cached("fw:" + fw, async () =>
    {
        var url = fw == ModFramework.Carbon
            ? "https://api.github.com/repos/CarbonCommunity/Carbon/releases/tags/production_build"
            : "https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest";
        var client = http.CreateClient("downloads");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var field in new[] { "tag_name", "name", "body" })
        {
            if (doc.RootElement.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var m = VersionRegex().Match(el.GetString() ?? "");
                if (m.Success) return m.Value;
            }
        }
        return null;
    });

    /// <summary>True when <paramref name="latest"/> is a higher dotted version than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? latest, string? installed)
    {
        if (latest is null || installed is null) return false;
        return Version.TryParse(latest, out var l) && Version.TryParse(installed, out var i) && l > i;
    }

    [GeneratedRegex("\"buildid\"\\s+\"(\\d+)\"")]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex(@"\d+\.\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionRegex();
}
