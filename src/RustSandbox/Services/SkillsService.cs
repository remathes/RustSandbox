using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed class SkillLevel
{
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("xp")] public double Xp { get; set; }
    [JsonPropertyName("next")] public double Next { get; set; }
    [JsonPropertyName("points")] public int Points { get; set; }
}

public sealed class SkillsPlayer
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("skills")] public Dictionary<string, SkillLevel> Skills { get; set; } = new();
    [JsonPropertyName("perks")] public Dictionary<string, int> Perks { get; set; } = new();
    [JsonPropertyName("totals")] public Dictionary<string, double> Totals { get; set; } = new();

    public SkillLevel Level(string skill) => Skills.TryGetValue(skill, out var l) ? l : new SkillLevel { Level = 1 };
}

public sealed class SkillsSummary
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxLevel")] public int MaxLevel { get; set; }
    [JsonPropertyName("xpRate")] public double XpRate { get; set; } = 1;
    [JsonPropertyName("xpMinutesLeft")] public double? XpMinutesLeft { get; set; }
    [JsonPropertyName("skills")] public List<string> Skills { get; set; } = new();
    [JsonPropertyName("avgLevel")] public Dictionary<string, double> AvgLevel { get; set; } = new();
    [JsonPropertyName("perkOwners")] public Dictionary<string, int> PerkOwners { get; set; } = new();

    public bool EventOn => XpRate > 1;
}

public sealed record SkillInfo(string Id, string Name, string Icon);
public sealed record PerkInfo(string Id, string Skill, string Name, string Effect);

/// <summary>
/// Talks to the RustSandboxSkills plugin: leaderboards, player details, admin XP grants and XP events
/// over RCON, and the balance settings through its config file (servers this machine runs).
/// </summary>
public sealed class SkillsService(ServerConnectionManager conn, ILogger<SkillsService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Skills;

    public static readonly SkillInfo[] Skills =
    {
        new("mining", "Mining", "pickaxe"), new("woodcutting", "Woodcutting", "hatchet"), new("combat", "Combat", "longsword"),
        new("gardening", "Gardening", "corn"), new("electrician", "Electrician", "wiretool"), new("survival", "Survival", "bandage"),
        new("mechanic", "Mechanic", "gears"), new("fishing", "Fishing", "fishingrod.handmade"), new("scrapper", "Scrapper", "scrap"),
        new("cooking", "Cooking", "bbq"), new("companion", "Companion", "wolfmeat.cooked"),
    };

    /// <summary>Names and what one rank does (the {0} is the bonus), matching the plugin's English text.</summary>
    public static readonly PerkInfo[] Perks =
    {
        new("prospector", "mining", "Prospector", "+{0}% ore and stone"),
        new("luckystrike", "mining", "Lucky Strike", "{0}% chance to double a hit"),
        new("toolcare", "mining", "Tool Care", "tools wear {0}% slower"),
        new("lumberjack", "woodcutting", "Lumberjack", "+{0}% wood"),
        new("craftsman", "woodcutting", "Craftsman", "craft {0}% faster"),
        new("applepicker", "woodcutting", "Apple Picker", "{0}% chance of 1 to 3 apples when a tree falls"),
        new("thickskin", "combat", "Thick Skin", "{0}% less damage taken"),
        new("hunter", "combat", "Hunter", "+{0}% damage to animals and NPCs"),
        new("fieldmedic", "combat", "Field Medic", "healing items heal {0}% more"),
        new("greenthumb", "gardening", "Green Thumb", "crops grow {0}% faster"),
        new("bountiful", "gardening", "Bountiful Harvest", "+{0}% from crops"),
        new("forager", "gardening", "Forager", "+{0}% from wild plants"),
        new("solarboost", "electrician", "Solar Boost", "solar panels make {0}% more power"),
        new("bigbatteries", "electrician", "Big Batteries", "batteries hold {0}% more"),
        new("generator", "electrician", "Efficient Generator", "generators burn {0}% less fuel"),
        new("ironstomach", "survival", "Iron Stomach", "hunger and thirst {0}% slower"),
        new("deepbreath", "survival", "Deep Breath", "lose air {0}% slower"),
        new("regeneration", "survival", "Regeneration", "heal {0} health a minute out of combat"),
        new("fuelsaver", "mechanic", "Fuel Saver", "vehicles burn {0}% less fuel"),
        new("toughframe", "mechanic", "Tough Frame", "vehicles take {0}% less damage"),
        new("quickfix", "mechanic", "Quick Fix", "repairs restore {0}% more"),
        new("bigcatch", "fishing", "Big Catch", "{0}% chance of a second fish"),
        new("steadyline", "fishing", "Steady Line", "line strains {0}% slower"),
        new("rodcare", "fishing", "Rod Care", "rods wear {0}% slower"),
        new("quickrecycler", "scrapper", "Quick Recycler", "recyclers run {0}% faster"),
        new("bargain", "scrapper", "Bargain Research", "research costs {0}% less"),
        new("techdiscount", "scrapper", "Tech Tree Discount", "{0}% scrap back on tech tree unlocks"),
        new("bigportions", "cooking", "Big Portions", "{0}% chance of an extra piece of cooked meat or fish"),
        new("masterbrewer", "cooking", "Master Brewer", "{0}% chance of extra teas from a mixing table"),
        new("steeped", "cooking", "Steeped", "tea and food buffs last {0}% longer"),
        new("howl", "companion", "Roar", "/pet roar (wolf, tiger, panther): hit {0}% more on top"),
        new("loyalfriend", "companion", "Loyal Friend", "your pet gets you up when downed, with {0}% health"),
        new("packhunter", "companion", "Pack Hunter", "+{0}% meat, leather and bone from your pet's kills"),
        new("bigpack", "companion", "Big Pack", "+{0} pet backpack slots"),
        new("keennose", "companion", "Keen Nose", "/pet sniff finds what's within {0} m"),
    };

    /// <summary>Perks whose bonus is a plain number, not a percentage.</summary>
    public static bool IsRawPerk(string id) => id is "regeneration" or "bigpack" or "keennose";

    public static string SkillName(string id) => Skills.FirstOrDefault(s => s.Id == id)?.Name ?? id;
    public static PerkInfo? Perk(string id) => Perks.FirstOrDefault(p => p.Id == id);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ------------------------------------------------------------------ RCON

    public async Task<SkillsSummary?> SummaryAsync(int serverId) =>
        Parse<SkillsSummary>(await conn.QueryAsync(serverId, "sandboxskills.summary", TimeSpan.FromSeconds(8)), '{');

    public async Task<List<SkillsPlayer>?> TopAsync(int serverId, int count, string? skill = null) =>
        Parse<List<SkillsPlayer>>(await conn.QueryAsync(serverId, $"sandboxskills.top {count} {skill}".TrimEnd(), TimeSpan.FromSeconds(10)), '[');

    public async Task<SkillsPlayer?> PlayerAsync(int serverId, string steamId)
    {
        var p = Parse<SkillsPlayer>(await conn.QueryAsync(serverId, $"sandboxskills.player {Safe(steamId)}", TimeSpan.FromSeconds(8)), '{');
        return string.IsNullOrEmpty(p?.Id) ? null : p;
    }

    /// <summary>Returns the plugin's reply, or null when the server is offline.</summary>
    public Task<string?> GiveXpAsync(int serverId, string steamId, string skill, double amount) =>
        conn.QueryAsync(serverId, $"sandboxskills.givexp {Safe(steamId)} {Safe(skill)} {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

    public Task<string?> ResetPlayerAsync(int serverId, string steamId) =>
        conn.QueryAsync(serverId, $"sandboxskills.reset {Safe(steamId)}");

    /// <summary>rate 1 stops the event; minutes 0 = until stopped.</summary>
    public Task<string?> SetXpRateAsync(int serverId, double rate, double minutes) =>
        conn.QueryAsync(serverId, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sandboxskills.xprate {rate} {minutes}"));

    /// <summary>The console command a schedule runs to start an XP event.</summary>
    public static string XpRateCommand(double rate, double minutes) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sandboxskills.xprate {rate} {minutes}");

    private static string Safe(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '.').ToArray());

    private T? Parse<T>(string? reply, char open) where T : class
    {
        if (string.IsNullOrEmpty(reply)) return null;
        var start = reply.IndexOf(open);
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<T>(reply[start..]); }
        catch (JsonException ex)
        {
            log.LogDebug(ex, "Couldn't read the skills plugin's reply");
            return null;
        }
    }

    // ------------------------------------------------------------------ config file (balance)

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    /// <summary>The plugin's config, or null with a reason when it can't be read here.</summary>
    public static async Task<(JsonObject? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "Balance settings are edited in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
        if (!File.Exists(path)) return (null, "The plugin hasn't created its config yet. Install it and start the server once.");
        try
        {
            return (JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject, null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Write the config and reload the plugin so it takes effect. Returns an error or null.</summary>
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
            log.LogWarning(ex, "Saving the skills config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }
}
