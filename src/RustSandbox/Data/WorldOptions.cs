using System.Globalization;
using System.Text.Json;

namespace RustSandbox.Data;

/// <summary>
/// Rust's built-in world generation settings (its WorldConfig): biome and tier mix, roads, rivers, rails and
/// which monuments/prefabs may spawn. Written to a file in the server's identity folder and passed with
/// +world.configfile. Rust only reads it when it generates a new map, so changes apply after a map wipe.
/// </summary>
public class WorldOptions
{
    public const string FileName = "sandbox_worldconfig.json";

    public bool Enabled { get; set; }

    // Percent. Tiers are how much of the map is easy (0), medium (1) and hard (2) ground.
    public float Tier0 { get; set; } = 30;
    public float Tier1 { get; set; } = 30;
    public float Tier2 { get; set; } = 40;

    // Percent of the land; Rust scales the four main biomes to add up to 100.
    public float Arid { get; set; } = 40;
    public float Temperate { get; set; } = 15;
    public float Tundra { get; set; } = 15;
    public float Arctic { get; set; } = 30;
    /// <summary>Share of the easy (tier 0) ground that turns into jungle.</summary>
    public float Jungle { get; set; } = 50;

    public bool MainRoads { get; set; } = true;
    public bool SideRoads { get; set; } = true;
    public bool Trails { get; set; } = true;
    public bool Rivers { get; set; } = true;
    public bool Powerlines { get; set; } = true;
    public bool AboveGroundRails { get; set; } = true;
    public bool BelowGroundRails { get; set; } = true;
    public bool UnderwaterLabs { get; set; } = true;

    /// <summary>Prefab name parts that must not spawn (monuments from the checklist plus anything typed in).</summary>
    public List<string> Blocked { get; set; } = new();

    /// <summary>Monuments and features that can be switched off, with the part of their prefab name Rust matches.</summary>
    public static readonly (string Match, string Name, string Group)[] Monuments =
    {
        ("launch_site", "Launch Site", "Large"),
        ("airfield", "Airfield", "Large"),
        ("military_tunnel", "Military Tunnels", "Large"),
        ("powerplant", "Power Plant", "Large"),
        ("trainyard", "Train Yard", "Large"),
        ("water_treatment_plant", "Water Treatment Plant", "Large"),
        ("excavator", "Giant Excavator", "Large"),
        ("nuclear_missile_silo", "Missile Silo", "Large"),
        ("arctic_research_base", "Arctic Research Base", "Large"),
        ("desert_military_base", "Abandoned Military Base", "Large"),
        ("ferry_terminal", "Ferry Terminal", "Large"),
        ("radtown_1", "Radtown", "Large"),
        ("jungle_ziggurat", "Jungle Ziggurat", "Large"),
        ("harbor_", "Harbors", "Coast"),
        ("oilrig_", "Oil Rigs", "Coast"),
        ("fishing_village", "Fishing Villages", "Coast"),
        ("lighthouse", "Lighthouses", "Coast"),
        ("compound", "Outpost", "Safe zones"),
        ("bandit_town", "Bandit Camp", "Safe zones"),
        ("stables_", "Ranch & Large Barn", "Safe zones"),
        ("satellite_dish", "Satellite Dish", "Medium"),
        ("sphere_tank", "The Dome", "Medium"),
        ("junkyard", "Junkyard", "Medium"),
        ("radtown_small", "Sewer Branch", "Medium"),
        ("gas_station", "Oxum's Gas Station", "Small"),
        ("supermarket", "Abandoned Supermarket", "Small"),
        ("warehouse", "Mining Outpost", "Small"),
        ("mining_quarry", "Quarries", "Small"),
        ("water_well", "Water Wells", "Small"),
        ("power_substation", "Power Substations", "Small"),
        ("cave_", "Caves", "Nature"),
        ("iceberg", "Icebergs", "Nature"),
        ("ice_lake", "Ice Lakes", "Nature"),
        ("swamp_", "Swamps", "Nature"),
    };

    /// <summary>The JSON Rust's WorldConfig reads (fractions, Rust's field names).</summary>
    public string ToRustJson()
    {
        static double F(float pct) => Math.Round(Math.Clamp(pct, 0, 100) / 100.0, 4);
        var blocked = Blocked.Select(b => b.Trim()).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var o = new Dictionary<string, object>
        {
            ["PercentageTier0"] = F(Tier0),
            ["PercentageTier1"] = F(Tier1),
            ["PercentageTier2"] = F(Tier2),
            ["PercentageBiomeArid"] = F(Arid),
            ["PercentageBiomeTemperate"] = F(Temperate),
            ["PercentageBiomeTundra"] = F(Tundra),
            ["PercentageBiomeArctic"] = F(Arctic),
            ["PercentageBiomeJungle"] = F(Jungle),
            ["MainRoads"] = MainRoads,
            ["SideRoads"] = SideRoads,
            ["Trails"] = Trails,
            ["Rivers"] = Rivers,
            ["Powerlines"] = Powerlines,
            ["AboveGroundRails"] = AboveGroundRails,
            ["BelowGroundRails"] = BelowGroundRails,
            ["UnderwaterLabs"] = UnderwaterLabs,
            ["PrefabBlacklist"] = blocked,
            ["PrefabWhitelist"] = new List<string>(),
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Writes the config file into server/&lt;identity&gt;. Returns an error or null.</summary>
    public static string? WriteFile(ServerSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.InstallDir)) return null;
        var dir = Path.Combine(s.InstallDir, "server", s.Identity);
        var path = Path.Combine(dir, FileName);
        try
        {
            if (!s.World.Enabled || s.MapMode != MapMode.Procedural)
            {
                if (File.Exists(path)) File.Delete(path);
                return null;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, s.World.ToRustJson());
            return null;
        }
        catch (Exception ex) { return $"Couldn't write {FileName}: {ex.Message}"; }
    }

    /// <summary>True when the server already has a generated map for this seed and size, so new world options wait for a map wipe.</summary>
    public static bool MapExists(ServerSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.InstallDir) || s.MapMode != MapMode.Procedural) return false;
        var dir = Path.Combine(s.InstallDir, "server", s.Identity);
        try
        {
            return Directory.Exists(dir) && Directory.GetFiles(dir, $"proceduralmap.{s.WorldSize.ToString(CultureInfo.InvariantCulture)}.{s.Seed.ToString(CultureInfo.InvariantCulture)}.*.map").Length > 0;
        }
        catch { return false; }
    }
}
