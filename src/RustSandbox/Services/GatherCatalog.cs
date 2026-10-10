namespace RustSandbox.Services;

/// <summary>Groups gathered items into the resource types shown on the Gathering page.</summary>
public static class GatherCatalog
{
    /// <summary>Display order.</summary>
    public static readonly string[] Categories =
    {
        "Wood", "Stone", "Metal", "Sulfur", "HQ Metal", "Berries", "Mushrooms", "Hemp & Cloth", "Crops", "Animal", "Fish", "Other",
    };

    public static string Category(string? shortname)
    {
        var s = (shortname ?? "").ToLowerInvariant();
        switch (s)
        {
            case "wood": return "Wood";
            case "stones": return "Stone";
            case "metal.ore": case "metal.fragments": return "Metal";
            case "sulfur.ore": case "sulfur": return "Sulfur";
            case "hq.metal.ore": case "metal.refined": return "HQ Metal";
            case "mushroom": return "Mushrooms";
            case "cloth": return "Hemp & Cloth";
            case "leather": case "fat.animal": case "bone.fragments": return "Animal";
            case "corn": case "pumpkin": case "potato": case "wheat": case "sunflower": case "rose": case "orchid": return "Crops";
        }
        if (s.EndsWith(".berry")) return "Berries";
        if (s.StartsWith("fish.")) return "Fish";
        if (s.EndsWith(".raw") || s.Contains("meat") || s.StartsWith("skull.")) return "Animal";
        if (s.StartsWith("seed.") || s.StartsWith("clone.")) return "Crops";
        return "Other";
    }

    public static string SourceLabel(string? source) => source switch
    {
        "tree" => "Chopping trees",
        "ore" => "Mining nodes",
        "animal" => "Harvesting animals",
        "pickup" => "Picked up",
        "farm" => "Farming",
        "fishing" => "Fishing",
        _ => "Other",
    };

    /// <summary>Accent colour per resource type, reusing the app's palette variables.</summary>
    public static string Color(string category) => category switch
    {
        "Wood" => "var(--hazard)",
        "Stone" => "var(--bone-dim)",
        "Metal" or "HQ Metal" => "var(--sky)",
        "Sulfur" => "var(--signal)",
        "Berries" or "Mushrooms" or "Hemp & Cloth" or "Crops" => "var(--patina)",
        _ => "var(--rust)",
    };
}
