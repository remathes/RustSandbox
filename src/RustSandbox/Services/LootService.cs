using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>One item in Rust's own loot table for a crate. Chance is 0-1: how often a crate holds at least one.</summary>
public sealed class VanillaLootItem
{
    [JsonPropertyName("s")] public string Short { get; set; } = "";
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("chance")] public double Chance { get; set; }
    [JsonPropertyName("min")] public int Min { get; set; } = 1;
    [JsonPropertyName("max")] public int Max { get; set; } = 1;
    [JsonPropertyName("bp")] public bool Blueprint { get; set; }
}

public sealed class VanillaCrate
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>crate (crates and barrels) or npc (what an NPC's body holds).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "crate";
    [JsonPropertyName("onMap")] public int OnMap { get; set; }
    [JsonPropertyName("slots")] public int Slots { get; set; }
    [JsonPropertyName("scrap")] public int Scrap { get; set; }
    [JsonPropertyName("items")] public List<VanillaLootItem> Items { get; set; } = new();
}

public sealed class VanillaLoot
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("heliCrates")] public int HeliCrates { get; set; } = -1;
    [JsonPropertyName("bradleyCrates")] public int BradleyCrates { get; set; } = -1;
    [JsonPropertyName("crates")] public List<VanillaCrate> Crates { get; set; } = new();
}

// The plugin's config file (its field names).
public sealed class LootItemConfig
{
    public string Item { get; set; } = "";
    public int Min { get; set; } = 1;
    public int Max { get; set; } = 1;
    public double Chance { get; set; } = 100;
    public bool Blueprint { get; set; }
    public ulong Skin { get; set; }
}

public sealed class CrateLootConfig
{
    /// <summary>Vanilla (Rust's own loot), Replace (only this list) or Add (this list on top of Rust's).</summary>
    public string Mode { get; set; } = "Vanilla";
    public int MinItems { get; set; } = 1;
    public int MaxItems { get; set; } = 6;
    /// <summary>Scrap in each crate; -1 = Rust's own amount.</summary>
    public int Scrap { get; set; } = -1;
    public List<LootItemConfig> Items { get; set; } = new();

    [JsonIgnore] public bool IsCustom => Mode is "Replace" or "Add" && Items.Count > 0;
}

public sealed class LootConfig
{
    public bool Enabled { get; set; } = true;
    public bool KeepScrap { get; set; } = true;
    /// <summary>Multiplies stackable items (and scrap) in crates and NPC bodies. 1 = Rust's amounts.</summary>
    public float LootMultiplier { get; set; } = 1f;
    /// <summary>Crates Patrol Heli / Bradley drop; -1 = Rust's own.</summary>
    public int HeliCrates { get; set; } = -1;
    public int BradleyCrates { get; set; } = -1;
    public Dictionary<string, CrateLootConfig> Crates { get; set; } = new();
}

/// <summary>Talks to the RustSandboxLoot plugin: custom tables in its config file, Rust's own tables over RCON (or its data file when the server is off).</summary>
public sealed class LootService(ServerConnectionManager conn, ILogger<LootService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Loot;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private readonly ConcurrentDictionary<int, VanillaLoot> _vanilla = new();

    /// <summary>Crates every map has, for when the plugin hasn't sent Rust's tables yet.</summary>
    public static readonly (string Id, string Name)[] CommonCrates =
    {
        ("crate_normal", "Military crate"), ("crate_normal_2", "Wooden crate"), ("crate_elite", "Elite crate"), ("crate_tools", "Tool box"),
        ("crate_basic", "Small crate"), ("crate_medical", "Medical crate"), ("crate_food_1", "Food crate"), ("crate_food_2", "Food crate (large)"),
        ("crate_underwater_basic", "Underwater crate"), ("crate_underwater_advanced", "Underwater crate (advanced)"),
        ("supply_drop", "Supply drop"), ("heli_crate", "Patrol Heli crate"), ("bradley_crate", "Bradley crate"),
        ("codelockedhackablecrate", "Locked crate"), ("codelockedhackablecrate_oilrig", "Locked crate (oil rig)"),
        ("loot_barrel_1", "Barrel (blue)"), ("loot_barrel_2", "Barrel (yellow)"), ("loot-barrel-1", "Barrel (road, blue)"),
        ("loot-barrel-2", "Barrel (road, yellow)"), ("oil_barrel", "Oil barrel"), ("minecart", "Mine cart"),
        ("scientistnpc_roam", "Scientist (roaming)"), ("scientistnpc_heavy", "Heavy scientist"), ("scientistnpc_oilrig", "Scientist (oil rig)"),
        ("npc_tunneldweller", "Tunnel dweller"), ("scarecrow", "Scarecrow"),
    };

    public static bool IsNpc(string id) => id.StartsWith("scientist") || id.StartsWith("npc_") || id.Contains("scarecrow");

    /// <summary>Rust's own loot tables: asked from the plugin when the server is up, else read from the file it saved last time.</summary>
    public async Task<(VanillaLoot? Loot, bool Live)> VanillaAsync(ServerInstance s, bool refresh = false)
    {
        if (!refresh && _vanilla.TryGetValue(s.Id, out var cached)) return (cached, true);
        if (conn.Get(s.Id).IsOnline)
        {
            var reply = await conn.QueryAsync(s.Id, "sandboxloot.export", TimeSpan.FromSeconds(20));
            var start = reply?.IndexOf('{') ?? -1;
            if (start >= 0)
            {
                try
                {
                    if (JsonSerializer.Deserialize<VanillaLoot>(reply![start..]) is { } live)
                    {
                        _vanilla[s.Id] = live;
                        return (live, true);
                    }
                }
                catch (JsonException ex) { log.LogDebug(ex, "Couldn't read the loot plugin's tables"); }
            }
        }
        if (DataPath(s) is { } path && File.Exists(path))
        {
            try { return (JsonSerializer.Deserialize<VanillaLoot>(await File.ReadAllTextAsync(path)), false); }
            catch (Exception ex) { log.LogDebug(ex, "Couldn't read {Path}", path); }
        }
        return (null, false);
    }

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    /// <summary>oxide/data (or carbon/data)/RustSandboxLoot_vanilla.json, written by the plugin when the server starts.</summary>
    public static string? DataPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p && Path.GetDirectoryName(p.Config) is { } root ? Path.Combine(root, "data", "RustSandboxLoot_vanilla.json") : null;

    public static async Task<(LootConfig? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "Custom loot lives in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
        if (!File.Exists(path)) return (new LootConfig(), null);
        try { return (JsonSerializer.Deserialize<LootConfig>(await File.ReadAllTextAsync(path), Lenient) ?? new LootConfig(), null); }
        catch (Exception ex) { return (null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>Write the config (keeping a .bak) and reload the plugin. Returns an error or null.</summary>
    public async Task<string?> SaveConfigAsync(ServerInstance s, LootConfig config)
    {
        var path = ConfigPath(s);
        if (path is null) return "This server isn't run by this machine.";
        try
        {
            foreach (var c in config.Crates.Values)
            {
                c.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Item));
                foreach (var i in c.Items)
                {
                    i.Min = Math.Max(1, i.Min);
                    i.Max = Math.Max(i.Min, i.Max);
                    i.Chance = Math.Clamp(Math.Round(i.Chance, 2), 0, 100);
                }
                c.MinItems = Math.Max(0, c.MinItems);
                c.MaxItems = Math.Max(c.MinItems, c.MaxItems);
            }
            config.LootMultiplier = (float)Math.Clamp(Math.Round(config.LootMultiplier, 2), 0.1, 100);
            // Crates left on Rust's loot with nothing listed or changed don't need to be in the file.
            foreach (var key in config.Crates.Where(kv => kv.Value.Mode == "Vanilla" && kv.Value.Items.Count == 0 && kv.Value.Scrap < 0).Select(kv => kv.Key).ToList())
                config.Crates.Remove(key);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, Indented));
            if (conn.Get(s.Id).IsOnline)
                await conn.QueryAsync(s.Id, $"{PluginService.Prefix(s.Settings)}reload {Plugin.PluginName}", TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving the loot config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>Re-roll the loot in crates already on the map (all of them, or one crate type).</summary>
    public Task<string?> RefillAsync(int serverId, string? crate = null) =>
        conn.QueryAsync(serverId, $"sandboxloot.refill {(string.IsNullOrWhiteSpace(crate) ? "all" : crate.Replace("\"", "").Trim())}", TimeSpan.FromSeconds(20));

    /// <summary>A custom list that starts as a copy of Rust's table for the crate, with the same chances.</summary>
    public static CrateLootConfig FromVanilla(VanillaCrate crate, int scrap = -1)
    {
        var items = crate.Items.Where(i => i.Short != "scrap").Select(i => new LootItemConfig
        {
            Item = i.Short, Min = Math.Max(1, i.Min), Max = Math.Max(1, i.Max), Chance = Math.Round(i.Chance * 100, 2), Blueprint = i.Blueprint,
        }).ToList();
        var expected = items.Sum(i => i.Chance) / 100;
        return new CrateLootConfig
        {
            Mode = "Replace",
            Scrap = scrap,
            MinItems = Math.Max(1, (int)Math.Floor(expected * 0.5)),
            MaxItems = Math.Max(1, crate.Slots > 0 ? Math.Min(crate.Slots, (int)Math.Ceiling(expected * 2) + 1) : 6),
            Items = items,
        };
    }
}
