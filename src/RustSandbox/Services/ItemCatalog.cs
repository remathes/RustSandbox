using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>An item from the Rust game's Bundles\items folder.</summary>
public sealed record CatalogItem(int ItemId, string Short, string Name, string Description, string Category, int Stack, string Rarity, bool HasIcon);

/// <summary>
/// Reads item details and icons from the Rust game (client) install: Bundles\items has one
/// &lt;shortname&gt;.json and &lt;shortname&gt;.png per item. Works offline, no plugin needed.
/// </summary>
public static partial class ItemCatalog
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, Dictionary<string, CatalogItem> Items)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static (DateTime Checked, string? Dir) _detected;

    /// <summary>The Bundles\items folder for a server, from its setting or auto-detected.</summary>
    public static string? ItemsDir(ServerSettings s)
    {
        var root = string.IsNullOrWhiteSpace(s.RustClientDir) ? DetectClientDir() : s.RustClientDir.Trim().Trim('"');
        if (string.IsNullOrEmpty(root)) return null;
        // Accept either the game folder or the items folder itself.
        foreach (var dir in new[] { Path.Combine(root, "Bundles", "items"), root })
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "wood.json"))) return dir;
        return null;
    }

    /// <summary>Find the Rust game folder in the usual Steam locations and Steam library folders.</summary>
    public static string? DetectClientDir()
    {
        if (DateTime.UtcNow - _detected.Checked < TimeSpan.FromMinutes(5)) return _detected.Dir;
        string? found = null;
        try
        {
            var steamRoots = new List<string>();
            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
                if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } pf) steamRoots.Add(Path.Combine(pf, "Steam"));
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
                steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            }
            steamRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam"));

            var libraries = new List<string>(steamRoots);
            foreach (var root in steamRoots)
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in VdfPathRegex().Matches(File.ReadAllText(vdf)))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            found = libraries.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(l => Path.Combine(l, "steamapps", "common", "Rust"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Bundles", "items", "wood.json")));
        }
        catch { /* best effort */ }
        _detected = (DateTime.UtcNow, found);
        return found;
    }

    /// <summary>All items keyed by shortname (newest file wins when Rust left old copies behind).</summary>
    public static Dictionary<string, CatalogItem> Load(ServerSettings s)
    {
        var dir = ItemsDir(s);
        if (dir is null) return new();
        var stamp = Directory.GetLastWriteTimeUtc(dir);
        if (Cache.TryGetValue(dir, out var c) && c.Stamp == stamp) return c.Items;

        var byId = new Dictionary<int, (DateTime Modified, CatalogItem Item)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                if (!r.TryGetProperty("shortname", out var sn) || sn.GetString() is not { Length: > 0 } shortname) continue;
                // Older game versions left files like "2module car.json" next to "2module.car.json"; skip names with spaces.
                if (shortname.Contains(' ')) continue;
                var id = r.TryGetProperty("itemid", out var idEl) && idEl.TryGetInt32(out var idv) ? idv : shortname.GetHashCode();
                var item = new CatalogItem(
                    id, shortname,
                    Str(r, "Name") ?? shortname,
                    Str(r, "Description") ?? "",
                    Str(r, "Category") ?? "Misc",
                    r.TryGetProperty("stackable", out var st) && st.TryGetInt32(out var stv) ? stv : 1,
                    Str(r, "rarity") ?? "",
                    File.Exists(Path.ChangeExtension(file, ".png")));
                var modified = File.GetLastWriteTimeUtc(file);
                if (!byId.TryGetValue(id, out var existing) || modified > existing.Modified) byId[id] = (modified, item);
            }
            catch { /* skip unreadable file */ }
        }
        var items = byId.Values.Select(v => v.Item).GroupBy(i => i.Short, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Cache[dir] = (stamp, items);
        return items;
    }

    /// <summary>Full path of an item's icon, or null. Only shortnames from the catalog are accepted.</summary>
    public static string? IconPath(ServerSettings s, string shortname)
    {
        var dir = ItemsDir(s);
        if (dir is null || !Load(s).ContainsKey(shortname)) return null;
        var path = Path.Combine(dir, shortname + ".png");
        return File.Exists(path) ? path : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"""path""\s*""([^""]+)""")]
    private static partial Regex VdfPathRegex();
}
