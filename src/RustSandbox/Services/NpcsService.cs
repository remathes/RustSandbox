using System.Text.Json;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>A preset in the RustSandboxNpcs plugin's config: what spawns and what it wears and carries.</summary>
public sealed class NpcPreset
{
    public string Name { get; set; } = "";
    /// <summary>Scientist or Animal.</summary>
    public string Kind { get; set; } = "Scientist";
    /// <summary>roam (normal scientist) or heavy.</summary>
    public string Body { get; set; } = "roam";
    public List<string> Animals { get; set; } = new();
    /// <summary>0 = Rust's own health.</summary>
    public float Health { get; set; }
    public float Damage { get; set; } = 1f;
    public float Accuracy { get; set; } = 1f;
    public List<List<string>> Outfits { get; set; } = new();
    public List<string> Weapons { get; set; } = new();
    public int WeaponDrop { get; set; } = 10;
    public bool KeepRustGear { get; set; }

    [JsonIgnore] public bool IsAnimal => string.Equals(Kind, "Animal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A group of NPCs at a monument (every copy of it) or a place picked in game.</summary>
public sealed class NpcSpot
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Preset { get; set; } = "scientists";
    public int Count { get; set; } = 4;
    /// <summary>0 = fit the monument.</summary>
    public float Radius { get; set; }
    public float RespawnMinutes { get; set; } = 10f;
    public string Monument { get; set; } = "";
    public string Position { get; set; } = "";
    public string Map { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class NpcRoads
{
    public int Groups { get; set; } = 3;
    public int Size { get; set; } = 3;
    public string Preset { get; set; } = "raiders";
    public float RespawnMinutes { get; set; } = 15f;
}

public sealed class NpcCamps
{
    /// <summary>Off, Few, Some or Many.</summary>
    public string Amount { get; set; } = "Some";
    public string Preset { get; set; } = "bandits";
    public int MinSize { get; set; } = 2;
    public int MaxSize { get; set; } = 4;
    public float RespawnMinutes { get; set; } = 20f;
    public bool Campfire { get; set; } = true;
}

/// <summary>The RustSandboxNpcs plugin's config file (its field names).</summary>
public sealed class NpcsConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Easy, Normal or Hard.</summary>
    public string Difficulty { get; set; } = "Normal";
    public Dictionary<string, NpcPreset> Presets { get; set; } = new();
    public List<NpcSpot> Spots { get; set; } = new();
    public NpcRoads Roads { get; set; } = new();
    public NpcCamps Camps { get; set; } = new();
}

public sealed class NpcMonument
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Copies { get; set; }
    public List<string> Grids { get; set; } = new();
}

public sealed class NpcGroupStatus
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("spot")] public string? Spot { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("preset")] public string Preset { get; set; } = "";
    [JsonPropertyName("alive")] public int Alive { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("grid")] public string Grid { get; set; } = "";
}

/// <summary>What the plugin reports over RCON (sandboxnpcs.status).</summary>
public sealed class NpcsStatus
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "";
    [JsonPropertyName("map")] public string Map { get; set; } = "";
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("alive")] public int Alive { get; set; }
    [JsonPropertyName("groups")] public List<NpcGroupStatus> Groups { get; set; } = new();
    [JsonPropertyName("monuments")] public List<NpcMonument> Monuments { get; set; } = new();
}

/// <summary>Talks to the RustSandboxNpcs plugin: settings in its config file, live counts and the monument list over RCON.</summary>
public sealed class NpcsService(ServerConnectionManager conn, ILogger<NpcsService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Npcs;

    public static readonly string[] Difficulties = { "Easy", "Normal", "Hard" };
    public static readonly string[] CampAmounts = { "Off", "Few", "Some", "Many" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    /// <summary>oxide/data/RustSandboxNpcs_monuments.json, written by the plugin each time it starts.</summary>
    public static string? MonumentsPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p && Path.GetDirectoryName(p.Config) is { } root ? Path.Combine(root, "data", Plugin.PluginName + "_monuments.json") : null;

    public static async Task<(NpcsConfig? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "NPC settings live in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
        if (!File.Exists(path)) return (null, "The NPCs plugin hasn't made its settings file yet. Install it from Telemetry → NPCs and start the server once.");
        try
        {
            var c = JsonSerializer.Deserialize<NpcsConfig>(await File.ReadAllTextAsync(path), Lenient) ?? new NpcsConfig();
            c.Roads ??= new NpcRoads();
            c.Camps ??= new NpcCamps();
            return (c, null);
        }
        catch (Exception ex) { return (null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>The monuments on the current map, from the last time the plugin ran.</summary>
    public static async Task<(string Map, List<NpcMonument> Monuments)> ReadSavedMonumentsAsync(ServerInstance s)
    {
        var path = MonumentsPath(s);
        if (path is null || !File.Exists(path)) return ("", new());
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var map = doc.RootElement.TryGetProperty("Map", out var m) ? m.GetString() ?? "" : "";
            var list = doc.RootElement.TryGetProperty("Monuments", out var arr)
                ? arr.Deserialize<List<NpcMonument>>(Lenient) ?? new()
                : new();
            return (map, list);
        }
        catch { return ("", new()); }
    }

    /// <summary>Write the config (keeping a .bak) and reload the plugin, which respawns every NPC. Returns an error or null.</summary>
    public async Task<string?> SaveConfigAsync(ServerInstance s, NpcsConfig config)
    {
        var path = ConfigPath(s);
        if (path is null) return "This server isn't run by this machine.";
        try
        {
            foreach (var spot in config.Spots)
            {
                spot.Count = Math.Clamp(spot.Count, 1, 30);
                spot.Radius = Math.Clamp(spot.Radius, 0, 200);
                spot.RespawnMinutes = Math.Clamp(spot.RespawnMinutes, 0.5f, 600);
            }
            config.Roads.Groups = Math.Clamp(config.Roads.Groups, 0, 12);
            config.Roads.Size = Math.Clamp(config.Roads.Size, 1, 8);
            config.Camps.MinSize = Math.Clamp(config.Camps.MinSize, 1, 8);
            config.Camps.MaxSize = Math.Clamp(config.Camps.MaxSize, config.Camps.MinSize, 10);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, Indented));
            if (conn.Get(s.Id).IsOnline)
                await conn.QueryAsync(s.Id, $"{PluginService.Prefix(s.Settings)}reload {Plugin.PluginName}", TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving the NPCs config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }

    public async Task<NpcsStatus?> StatusAsync(int serverId)
    {
        if (!conn.Get(serverId).IsOnline) return null;
        var reply = await conn.QueryAsync(serverId, "sandboxnpcs.status", TimeSpan.FromSeconds(10));
        var start = reply?.IndexOf('{') ?? -1;
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<NpcsStatus>(reply![start..]); }
        catch (JsonException ex) { log.LogDebug(ex, "Couldn't read the NPCs plugin's status"); return null; }
    }

    public Task<string?> RespawnAsync(int serverId) =>
        conn.QueryAsync(serverId, "sandboxnpcs.respawn", TimeSpan.FromSeconds(10));

    public static string NewId(NpcsConfig c)
    {
        string id;
        do id = "s" + Random.Shared.Next(1000, 99999);
        while (c.Spots.Any(x => x.Id == id));
        return id;
    }
}
