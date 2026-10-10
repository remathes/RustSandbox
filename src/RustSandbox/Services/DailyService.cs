using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed class DailyWin
{
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("itemName")] public string ItemName { get; set; } = "";
    [JsonPropertyName("amount")] public int Amount { get; set; }
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
}

public sealed class DailyStreak
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("streak")] public int Streak { get; set; }
    [JsonPropertyName("claims")] public int Claims { get; set; }
}

public sealed class DailySummary
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("today")] public string Today { get; set; } = "";
    [JsonPropertyName("nextResetMinutes")] public double NextResetMinutes { get; set; }
    [JsonPropertyName("claimsToday")] public int ClaimsToday { get; set; }
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("streaks")] public List<DailyStreak> Streaks { get; set; } = new();
    [JsonPropertyName("history")] public List<DailyWin> History { get; set; } = new();
}

/// <summary>Talks to the RustSandboxDaily plugin: winners and streaks over RCON, the loot table through its config file.</summary>
public sealed class DailyService(ServerConnectionManager conn, ILogger<DailyService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Daily;
    public const string TiersKey = "Tiers (rarest last)";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<DailySummary?> SummaryAsync(int serverId, int count = 100)
    {
        var reply = await conn.QueryAsync(serverId, $"sandboxdaily.summary {count}", TimeSpan.FromSeconds(8));
        var start = reply?.IndexOf('{') ?? -1;
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<DailySummary>(reply![start..]); }
        catch (JsonException ex)
        {
            log.LogDebug(ex, "Couldn't read the daily rewards plugin's reply");
            return null;
        }
    }

    /// <summary>Lets a player claim again today. Returns the plugin's reply, or null when the server is offline.</summary>
    public Task<string?> ResetClaimAsync(int serverId, string steamId) =>
        conn.QueryAsync(serverId, $"sandboxdaily.reset {new string(steamId.Where(char.IsDigit).ToArray())}");

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    public static async Task<(JsonObject? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "The loot table lives in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
        if (!File.Exists(path)) return (null, "The plugin hasn't created its config yet. Install it from the Telemetry page and start the server once.");
        try { return (JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject, null); }
        catch (Exception ex) { return (null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>Write the config (keeping a .bak) and reload the plugin. Returns an error or null.</summary>
    public async Task<string?> SaveConfigAsync(ServerInstance s, JsonObject config)
    {
        var path = ConfigPath(s);
        if (path is null) return "This server isn't run by this machine.";
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            await File.WriteAllTextAsync(path, config.ToJsonString(Indented));
            if (conn.Get(s.Id).IsOnline)
                await conn.QueryAsync(s.Id, $"{PluginService.Prefix(s.Settings)}reload {Plugin.PluginName}", TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving the daily rewards config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>"0.75 0.35 0.95" ↔ "#bf59f2" for colour pickers.</summary>
    public static string ToHex(string? rgb)
    {
        var p = (rgb ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int C(int i) => p.Length > i && double.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(Math.Clamp(v, 0, 1) * 255) : 180;
        return $"#{C(0):x2}{C(1):x2}{C(2):x2}";
    }

    public static string FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return "0.7 0.7 0.7";
        string C(int i) => (Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"{C(0)} {C(2)} {C(4)}";
    }
}
