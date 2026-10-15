using System.Text.Json;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>The RustSandboxStore plugin's config file (its field names). Unknown fields are kept.</summary>
public sealed class StoreConfig
{
    public bool Enabled { get; set; } = true;
    public string CurrencyName { get; set; } = "Rust Credits";
    public string Short { get; set; } = "RC";
    public double StartingBalance { get; set; } = 50;
    public bool ShowEarnings { get; set; } = true;
    public bool AllowPay { get; set; } = true;
    public bool ResetOnWipe { get; set; }
    public bool MapMarkers { get; set; } = true;
    public int MaxPerLine { get; set; } = 20;
    /// <summary>Wood, Stone, Metal or TopTier: what the strip mall is built from.</summary>
    public string BuildGrade { get; set; } = "Wood";
    public bool NeonSigns { get; set; } = true;
    public StoreEarn Earn { get; set; } = new();
    public List<StoreShop> Shops { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StoreEarn
{
    public double PlayerKill { get; set; } = 25;
    public double PlayerKillCooldownMinutes { get; set; } = 10;
    public double NpcKill { get; set; } = 8;
    public double AnimalKill { get; set; } = 3;
    public Dictionary<string, double> Kills { get; set; } = new();
    public Dictionary<string, double> GatherPer100 { get; set; } = new();
    public double FarmHarvest { get; set; } = 1;
    public double Pickup { get; set; } = 0.5;
    public double Fish { get; set; } = 4;
    public double FishJunk { get; set; } = 1;
    public Dictionary<string, double> Keycards { get; set; } = new();
    public double KeycardCooldownMinutes { get; set; } = 30;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StoreShop
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Keeper { get; set; } = "";
    public string Look { get; set; } = "outpost_a";
    public string Color { get; set; } = "0.85 0.62 0.20";
    public bool Enabled { get; set; } = true;
    public List<StoreItem> Items { get; set; } = new();
    /// <summary>What the strip mall shows: a gun or tool per entry goes on a wall rack, a comma list of clothes dresses a mannequin, anything else that can be placed stands on the floor.</summary>
    public List<string> Displays { get; set; } = new();
    /// <summary>An open-air stall (no walls or roof): seeds show as planters of crops, food lists as market tables.</summary>
    public bool? Outdoor { get; set; }
    /// <summary>The neon sign picture as base64 PNG; blank uses the plugin's built-in sign.</summary>
    public string? Sign { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StoreItem
{
    public string Item { get; set; } = "";
    public int Amount { get; set; } = 1;
    public int Price { get; set; } = 10;
    public string Category { get; set; } = "";
    public ulong Skin { get; set; }
    public bool Blueprint { get; set; }
    public string Name { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What the plugin reports (sandboxstore.status).</summary>
public sealed class StoreStatus
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("short")] public string Short { get; set; } = "RC";
    [JsonPropertyName("totalCredits")] public long TotalCredits { get; set; }
    [JsonPropertyName("vendors")] public List<StoreVendor> Vendors { get; set; } = new();
    [JsonPropertyName("players")] public List<StorePlayer> Players { get; set; } = new();
    [JsonPropertyName("log")] public List<StoreLog> Log { get; set; } = new();
    [JsonPropertyName("malls")] public List<StoreMall> Malls { get; set; } = new();
    [JsonPropertyName("missingItems")] public List<string> MissingItems { get; set; } = new();
}

public sealed class StoreVendor
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("shop")] public string Shop { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("monument")] public string Monument { get; set; } = "";
    [JsonPropertyName("onMap")] public bool OnMap { get; set; }
    [JsonPropertyName("grid")] public string Grid { get; set; } = "";
}

public sealed class StoreMall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("shops")] public List<string> Shops { get; set; } = new();
    [JsonPropertyName("onMap")] public bool OnMap { get; set; }
    [JsonPropertyName("grid")] public string Grid { get; set; } = "";
}

public sealed class StorePlayer
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("balance")] public long Balance { get; set; }
    [JsonPropertyName("earned")] public long Earned { get; set; }
    [JsonPropertyName("spent")] public long Spent { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("earnedBy")] public Dictionary<string, double> EarnedBy { get; set; } = new();
}

public sealed class StoreLog
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>Talks to the RustSandboxStore plugin: shops and earning rates in its config file, balances over RCON.</summary>
public sealed class StoreService(ServerConnectionManager conn, ILogger<StoreService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Store;

    /// <summary>The talking-NPC looks a shopkeeper can have (Rust's mission NPC prefabs).</summary>
    public static readonly string[] Looks = { "outpost_a", "outpost_b", "bandit_a", "bandit_b", "fishing_a", "fishing_b", "stables_a", "stables_b", "generic_a", "floatingcity_a" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    /// <summary>The config, or null with no error when the plugin hasn't written one yet (install it and start the server once).</summary>
    public static async Task<(StoreConfig? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "Store settings live in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
        if (!File.Exists(path)) return (null, null);
        try
        {
            var c = JsonSerializer.Deserialize<StoreConfig>(await File.ReadAllTextAsync(path), Lenient) ?? new StoreConfig();
            c.Earn ??= new StoreEarn();
            c.Shops ??= new();
            foreach (var shop in c.Shops)
            {
                shop.Items ??= new();
                shop.Displays ??= new();
            }
            return (c, null);
        }
        catch (Exception ex) { return (null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>Write the config (keeping a .bak) and reload the plugin, which respawns the shopkeepers. Returns an error or null.</summary>
    public async Task<string?> SaveConfigAsync(ServerInstance s, StoreConfig config)
    {
        var path = ConfigPath(s);
        if (path is null) return "This server isn't run by this machine.";
        try
        {
            foreach (var shop in config.Shops)
            {
                shop.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Item));
                foreach (var i in shop.Items)
                {
                    i.Item = i.Item.Trim();
                    i.Amount = Math.Max(1, i.Amount);
                    i.Price = Math.Max(0, i.Price);
                }
            }
            config.MaxPerLine = Math.Clamp(config.MaxPerLine, 1, 100);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, Indented));
            if (conn.Get(s.Id).IsOnline)
                await conn.QueryAsync(s.Id, $"{PluginService.Prefix(s.Settings)}reload {Plugin.PluginName}", TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving the store config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }

    public async Task<StoreStatus?> StatusAsync(int serverId)
    {
        if (!conn.Get(serverId).IsOnline) return null;
        var reply = await conn.QueryAsync(serverId, "sandboxstore.status", TimeSpan.FromSeconds(10));
        var start = reply?.IndexOf('{') ?? -1;
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<StoreStatus>(reply![start..]); }
        catch (JsonException ex) { log.LogDebug(ex, "Couldn't read the store plugin's status"); return null; }
    }

    /// <summary>give, take or set a player's credits.</summary>
    public Task<string?> CreditsAsync(int serverId, string kind, string steamId, long amount) =>
        conn.QueryAsync(serverId, $"sandboxstore.credits {kind} {steamId} {amount}", TimeSpan.FromSeconds(10));

    public Task<string?> RemoveMallAsync(int serverId, string id) =>
        conn.QueryAsync(serverId, $"sandboxstore.mall.remove {id}", TimeSpan.FromSeconds(10));

    public Task<string?> RespawnAsync(int serverId) =>
        conn.QueryAsync(serverId, "sandboxstore.respawn", TimeSpan.FromSeconds(10));

    /// <summary>A deep copy, so Discard can go back to what was loaded.</summary>
    public static StoreConfig Clone(StoreConfig c) =>
        JsonSerializer.Deserialize<StoreConfig>(JsonSerializer.Serialize(c))!;

    /// <summary>"0.78 0.27 0.22" (the plugin's colour format) to "#c74538" for a colour input, and back.</summary>
    public static string ToHex(string rgb)
    {
        var p = (rgb ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return "#888888";
        int C(string v) => Math.Clamp((int)Math.Round(double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d * 255 : 136), 0, 255);
        return $"#{C(p[0]):x2}{C(p[1]):x2}{C(p[2]):x2}";
    }

    public static string FromHex(string hex)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length != 6) return "0.5 0.5 0.5";
        string C(int i) => (Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return $"{C(0)} {C(2)} {C(4)}";
    }
}
