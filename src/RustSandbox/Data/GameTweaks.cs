using System.Text.Json.Serialization;

namespace RustSandbox.Data;

/// <summary>
/// Gather rates, stack sizes and smelting, applied in game by the RustSandboxTweaks plugin.
/// Stored with the server's settings; the manager writes it to the plugin's config file on Apply.
/// </summary>
public sealed class GameTweaks
{
    public bool Enabled { get; set; }

    // ---- Gather ----
    /// <summary>Base gather multiplier for every resource (1 = vanilla).</summary>
    public double GatherRate { get; set; } = 1;
    /// <summary>Per-resource rate that replaces GatherRate, keyed by item shortname (e.g. "wood").</summary>
    public Dictionary<string, double> GatherByItem { get; set; } = new();
    /// <summary>Extra factor by source. Final amount = vanilla × (item rate or GatherRate) × source factor.</summary>
    public double SourceHits { get; set; } = 1;       // chopping trees, mining nodes, harvesting corpses
    public double SourcePickups { get; set; } = 1;    // hemp, mushrooms, stones/wood on the ground
    public double SourceCrops { get; set; } = 1;      // planted crops
    public double SourceQuarries { get; set; } = 1;   // quarries, pump jacks, excavator, survey charges
    /// <summary>Add e.g. " [10x]" to the server name.</summary>
    public bool ShowRateInHostname { get; set; }

    // ---- Stacks ----
    public double StackRate { get; set; } = 1;
    /// <summary>Per-category multiplier keyed by Rust's ItemCategory name (Resources, Ammunition…). Replaces StackRate.</summary>
    public Dictionary<string, double> StackByCategory { get; set; } = new();
    /// <summary>Exact stack size per item shortname. Beats every multiplier and also applies to items that normally don't stack.</summary>
    public Dictionary<string, int> StackOverrides { get; set; } = new();

    // ---- Smelting / cooking ----
    /// <summary>How much faster ores and crude oil cook (2 = twice as fast).</summary>
    public double SmeltSpeed { get; set; } = 1;
    /// <summary>Output per item for ores and crude oil (2 = 1 metal ore gives 2 fragments).</summary>
    public double SmeltOutput { get; set; } = 1;
    /// <summary>How much faster food cooks on campfires, BBQs and furnaces.</summary>
    public double CookSpeed { get; set; } = 1;
    /// <summary>Per-oven fuel use, keyed by oven short prefab name. 0.5 = half the wood.</summary>
    public Dictionary<string, double> FuelUseByOven { get; set; } = new();
    public double FuelUse { get; set; } = 1;
    /// <summary>Charcoal made per burned wood (2 = double).</summary>
    public double Charcoal { get; set; } = 1;

    // ---- Inventory sorting (works whether or not Enabled is on) ----
    /// <summary>Sort buttons when looting boxes, barrels, crates, stashes, the tool cupboard and backpacks, plus /sort.</summary>
    public bool Sorting { get; set; }
    /// <summary>Where the Sort buttons sit, in Rust UI pixels from the bottom centre of the screen.</summary>
    public float SortButtonX { get; set; } = 193;
    public float SortButtonY { get; set; } = 18;
    /// <summary>Spread ore a player drops into a furnace evenly over its input slots (players can opt out with /autosplit).</summary>
    public bool FurnaceSplit { get; set; }
    /// <summary>/remove: hit your own building pieces and deployables with a Hammer to remove them.</summary>
    public bool RemoveTool { get; set; }
    /// <summary>Share of a building piece's cost refunded when it's removed (deployables come back whole).</summary>
    public double RemoveRefund { get; set; } = 0.5;
    public float RemoveSeconds { get; set; } = 60;
    /// <summary>Green/red screen edges in radiation. Lives in the Event HUD plugin's config; the manager sets it on Apply.</summary>
    public bool RadiationTint { get; set; }

    public static readonly (string Short, string Name)[] Resources =
    {
        ("wood", "Wood"), ("stones", "Stones"), ("metal.ore", "Metal ore"), ("sulfur.ore", "Sulfur ore"),
        ("hq.metal.ore", "High quality metal ore"), ("cloth", "Cloth"), ("leather", "Leather"),
        ("fat.animal", "Animal fat"), ("bone.fragments", "Bone fragments"), ("crude.oil", "Crude oil"),
        ("scrap", "Scrap"), ("metal.fragments", "Metal fragments"), ("charcoal", "Charcoal"),
    };

    public static readonly (string Prefab, string Name)[] Ovens =
    {
        ("furnace", "Furnace"), ("furnace.large", "Large furnace"), ("refinery_small_deployed", "Small oil refinery"),
        ("campfire", "Campfire"), ("bbq.deployed", "Barbeque"),
        ("fireplace.deployed", "Stone fireplace"), ("hobobarrel.deployed", "Hobo barrel"),
    };

    /// <summary>Short label like "10x" from the main gather rate.</summary>
    [JsonIgnore]
    public string RateLabel => $"{GatherRate:0.##}x";
}
