using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed class QuestGiverInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("look")] public string Look { get; set; } = "";
    [JsonPropertyName("grid")] public string Grid { get; set; } = "";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
    [JsonPropertyName("thisMap")] public bool ThisMap { get; set; }
    [JsonPropertyName("spawned")] public bool Spawned { get; set; }
}

public sealed class QuestActive
{
    [JsonPropertyName("quest")] public string Quest { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("progress")] public int Progress { get; set; }
    [JsonPropertyName("goal")] public int Goal { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
}

public sealed class QuestDone
{
    [JsonPropertyName("quest")] public string Quest { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("last")] public DateTime Last { get; set; }
}

public sealed class QuestPlayer
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("active")] public List<QuestActive> Active { get; set; } = new();
    [JsonPropertyName("done")] public List<QuestDone> Done { get; set; } = new();
}

public sealed class QuestCompletion
{
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("quest")] public string Quest { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

public sealed class QuestsSummary
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("returnToGiver")] public bool ReturnToGiver { get; set; }
    [JsonPropertyName("looks")] public List<string> Looks { get; set; } = new();
    [JsonPropertyName("givers")] public List<QuestGiverInfo> Givers { get; set; } = new();
    [JsonPropertyName("players")] public List<QuestPlayer> Players { get; set; } = new();
    [JsonPropertyName("history")] public List<QuestCompletion> History { get; set; } = new();
}

/// <summary>Talks to the RustSandboxQuests plugin: givers and progress over RCON, the quest list through its config file.</summary>
public sealed class QuestsService(ServerConnectionManager conn, ILogger<QuestsService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Quests;
    public const string QuestsKey = "Quests";

    // Keys in the plugin's config (its JsonProperty names).
    public const string IdKey = "Id";
    public const string TitleKey = "Title";
    public const string DescriptionKey = "Description";
    public const string EnabledKey = "Enabled";
    public const string GiverKey = "Giver (quest giver id, blank = every quest giver)";
    public const string TypeKey = "Type (Gather, Kill, Loot, Craft, Deliver, Swipe, Visit)";
    public const string TargetKey = "Target (item / prefab names, comma separated, * wildcards; keycard colour for Swipe; monument for Visit)";
    public const string AmountKey = "Amount";
    public const string WhereKey = "Where (monument name, blank = anywhere)";
    public const string ObjectiveKey = "Objective text (blank = automatic)";
    public const string RequiresKey = "Requires quest (id, blank = none)";
    public const string RepeatableKey = "Repeatable";
    public const string CooldownKey = "Cooldown before repeating (minutes)";
    public const string RewardsKey = "Rewards";
    public const string CommandsKey = "Reward console commands ({steamid}, {name})";

    public static readonly string[] Types = { "Gather", "Kill", "Loot", "Craft", "Deliver", "Swipe", "Visit" };

    /// <summary>Friendly names for kill targets; the value is what goes in the quest's Target.</summary>
    public static readonly (string Label, string Target)[] KillPresets =
    {
        ("Scientists", "scientistnpc_*"), ("Heavy scientists", "scientistnpc_heavy"), ("Tunnel dwellers", "npc_tunneldweller"),
        ("Underwater dwellers", "npc_underwaterdweller"), ("NPCs (any)", "npc"), ("Animals (any)", "animal"), ("Bears", "bear,polarbear"),
        ("Wolves", "wolf,wolf2"), ("Boars", "boar"), ("Deer", "stag"), ("Chickens", "chicken"), ("Tigers", "tiger"), ("Panthers", "panther"),
        ("Crocodiles", "crocodile"), ("Sharks", "simpleshark"), ("Bradley APCs", "bradleyapc"), ("Patrol Helicopters", "patrolhelicopter"),
    };

    public static readonly (string Label, string Target)[] LootPresets =
    {
        ("Crates (any)", "crate_*,codelockedhackablecrate*,supply_drop,bradley_crate,heli_crate"),
        ("Monument crates", "crate_normal,crate_normal_2,crate_elite,crate_tools"), ("Military crates", "crate_normal"),
        ("Basic crates", "crate_normal_2"), ("Elite crates", "crate_elite"), ("Tool boxes", "crate_tools"), ("Locked crates", "codelockedhackablecrate,codelockedhackablecrate_oilrig"),
        ("Supply drops", "supply_drop"), ("Barrels", "*barrel*"), ("Bradley crates", "bradley_crate"), ("Heli crates", "heli_crate"),
        ("Food crates", "crate_food_1,crate_food_2,foodbox"), ("Medical crates", "crate_medical"), ("Underwater crates", "crate_underwater_basic,crate_underwater_advanced"),
    };

    /// <summary>"Scientists" → "scientists", "Crates (any)" → "crates", "Bradley APCs" stays.</summary>
    public static string Noun(string label)
    {
        var s = label.Replace(" (any)", "");
        return s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) && !s.StartsWith("Bradley") && !s.StartsWith("Patrol") ? char.ToLower(s[0]) + s[1..] : s;
    }

    public static readonly string[] Monuments =
    {
        "Launch Site", "Military Tunnel", "Airfield", "Train Yard", "Water Treatment Plant", "Power Plant", "Satellite Dish", "The Dome",
        "Harbor", "Large Oil Rig", "Oil Rig", "Lighthouse", "Junkyard", "Sewer Branch", "Gas Station", "Supermarket", "Mining Outpost",
        "Outpost", "Bandit Camp", "Arctic Research Base", "Ferry Terminal", "Giant Excavator", "Missile Silo", "Abandoned Military Base", "Fishing Village",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<QuestsSummary?> SummaryAsync(int serverId, int count = 100)
    {
        var reply = await conn.QueryAsync(serverId, $"sandboxquests.summary {count}", TimeSpan.FromSeconds(8));
        var start = reply?.IndexOf('{') ?? -1;
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<QuestsSummary>(reply![start..]); }
        catch (JsonException ex)
        {
            log.LogDebug(ex, "Couldn't read the quests plugin's reply");
            return null;
        }
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
    private static string Arg(string s) => (s ?? "").Replace("\"", "").Replace("\n", " ").Replace("\r", " ").Trim();

    public Task<string?> AddGiverAsync(int serverId, string steamId, string name) =>
        conn.QueryAsync(serverId, $"sandboxquests.giver.add {Digits(steamId)} {Arg(name)}", TimeSpan.FromSeconds(10));

    public Task<string?> MoveGiverAsync(int serverId, string giverId, string steamId) =>
        conn.QueryAsync(serverId, $"sandboxquests.giver.move {Arg(giverId)} {Digits(steamId)}", TimeSpan.FromSeconds(10));

    public Task<string?> RenameGiverAsync(int serverId, string giverId, string name) =>
        conn.QueryAsync(serverId, $"sandboxquests.giver.rename {Arg(giverId)} {Arg(name)}");

    public Task<string?> SetLookAsync(int serverId, string giverId, string look) =>
        conn.QueryAsync(serverId, $"sandboxquests.giver.look {Arg(giverId)} {Arg(look)}", TimeSpan.FromSeconds(10));

    public Task<string?> RemoveGiverAsync(int serverId, string giverId) =>
        conn.QueryAsync(serverId, $"sandboxquests.giver.remove {Arg(giverId)}");

    /// <summary>Clears a player's progress on one quest (or all of them when questId is null).</summary>
    public Task<string?> ResetAsync(int serverId, string steamId, string? questId) =>
        conn.QueryAsync(serverId, $"sandboxquests.reset {Digits(steamId)} {Arg(questId ?? "")}".TrimEnd());

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    public static async Task<(JsonObject? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "Quests live in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
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
            log.LogWarning(ex, "Saving the quests config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>A new quest with the plugin's defaults, ready to edit.</summary>
    public static JsonObject NewQuest(string id, string type = "Gather") => new()
    {
        [IdKey] = id, [TitleKey] = "New quest", [DescriptionKey] = "", [EnabledKey] = true, [GiverKey] = "",
        [TypeKey] = type, [TargetKey] = type == "Gather" ? "stones" : "", [AmountKey] = 1, [WhereKey] = "", [ObjectiveKey] = "",
        [RequiresKey] = "", [RepeatableKey] = false, [CooldownKey] = 60.0, [RewardsKey] = new JsonArray(), [CommandsKey] = new JsonArray(),
    };

    public static string LookLabel(string look) => look switch
    {
        "outpost_a" => "Outpost A",
        "outpost_b" => "Outpost B",
        "bandit_a" => "Bandit A",
        "bandit_b" => "Bandit B",
        "fishing_a" => "Fisher A",
        "fishing_b" => "Fisher B",
        "stables_a" => "Stables A",
        "stables_b" => "Stables B",
        "generic_a" => "Generic",
        "floatingcity_a" => "Floating City",
        _ => look,
    };
}
