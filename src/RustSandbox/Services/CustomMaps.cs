using System.Text;
using System.Text.Json;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>A .map file the manager can host, with its optional live-map image and details (Name.jpg/.png and Name.json next to it).</summary>
public sealed record CustomMapInfo(string FileName, string FullPath, long Bytes, bool Bundled, string Title, string Description, int WorldSize, string? ImagePath)
{
    public string BaseName => Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>Details stored next to a map (Name.json).</summary>
public sealed class CustomMapDetails
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int WorldSize { get; set; }
    public List<CustomMapMonument> Monuments { get; set; } = new();
    /// <summary>Dry-land spots (world x, z) the demo server's simulated players gather around.</summary>
    public List<float[]> LandSpots { get; set; } = new();
}

public sealed class CustomMapMonument
{
    public string Name { get; set; } = "";
    public string Prefab { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
}

/// <summary>
/// The manager's map library. Maps that ship with the app live in its "maps" folder; uploaded ones go to the
/// data folder's "maps" folder. The manager serves them at /mapfiles/&lt;name&gt;.map so Rust and its players can download them.
/// </summary>
public static class CustomMaps
{
    /// <summary>The map new servers and the demo server start on.</summary>
    public const string DefaultMap = "Maui.map";
    public const string DownloadPrefix = "mapfiles";
    public const long MaxUploadBytes = 300L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Maps shipped with the app (read-only).</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "maps");

    /// <summary>Where uploaded maps go.</summary>
    public static string LibraryDir => Path.Combine(AppPaths.DataDir ?? AppContext.BaseDirectory, "maps");

    /// <summary>The manager's own address (Preferences → Public URL), used when a server has no map download address of its own.</summary>
    public static string DefaultHostUrl { get; set; } = "http://localhost:5080";

    public static IReadOnlyList<CustomMapInfo> List()
    {
        // An uploaded map with the same name as a bundled one wins, like in Find.
        var found = new Dictionary<string, CustomMapInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { LibraryDir, BundledDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.map"))
                found.TryAdd(f.Name, Describe(f, File.Exists(Path.Combine(BundledDir, f.Name))));
        }
        return found.Values.OrderByDescending(m => m.Bundled).ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static CustomMapInfo? Find(string? fileName)
    {
        if (!IsSafeName(fileName)) return null;
        foreach (var dir in new[] { LibraryDir, BundledDir })
        {
            var path = Path.Combine(dir, fileName!);
            if (File.Exists(path)) return Describe(new FileInfo(path), File.Exists(Path.Combine(BundledDir, fileName!)));
        }
        return null;
    }

    public static CustomMapDetails? Details(CustomMapInfo map)
    {
        var json = Path.ChangeExtension(map.FullPath, ".json");
        if (!File.Exists(json)) return null;
        try { return JsonSerializer.Deserialize<CustomMapDetails>(File.ReadAllText(json), Json); }
        catch { return null; }
    }

    /// <summary>A plain file name such as "Maui.map": no folders, no tricks.</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
        && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.StartsWith('.');

    /// <summary>A safe library file name for an uploaded map.</summary>
    public static string CleanName(string uploaded)
    {
        var name = new string(Path.GetFileNameWithoutExtension(uploaded).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return (name.Length == 0 ? "custom" : name) + ".map";
    }

    public static async Task<CustomMapInfo> SaveUploadAsync(string fileName, Stream data, CancellationToken ct = default)
    {
        var name = CleanName(fileName);
        Directory.CreateDirectory(LibraryDir);
        var path = Path.Combine(LibraryDir, name);
        var tmp = path + ".upload";
        await using (var fs = File.Create(tmp))
            await data.CopyToAsync(fs, ct);
        File.Move(tmp, path, overwrite: true);
        return Find(name)!;
    }

    /// <summary>Deletes an uploaded map. Maps that ship with the app can't be deleted.</summary>
    public static bool Delete(string fileName)
    {
        if (!IsSafeName(fileName) || File.Exists(Path.Combine(BundledDir, fileName))) return false;
        var path = Path.Combine(LibraryDir, fileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        foreach (var ext in new[] { ".jpg", ".png", ".json" })
            if (File.Exists(Path.ChangeExtension(path, ext))) File.Delete(Path.ChangeExtension(path, ext));
        return true;
    }

    /// <summary>Base address (scheme://host:port) players download this server's map from.</summary>
    public static string HostUrl(ServerSettings s) =>
        (string.IsNullOrWhiteSpace(s.MapHostUrl) ? DefaultHostUrl : s.MapHostUrl.Trim()).TrimEnd('/');

    /// <summary>The server.levelurl Rust gets: the hosted library map, or the pasted URL.</summary>
    public static string LevelUrl(ServerSettings s) =>
        !string.IsNullOrWhiteSpace(s.CustomMapFile)
            ? $"{HostUrl(s)}/{DownloadPrefix}/{Uri.EscapeDataString(s.CustomMapFile.Trim())}"
            : s.LevelUrl.Trim();

    /// <summary>A suggested download address for friends joining from outside: the join address with the manager's port.</summary>
    public static string? SuggestHostUrl(ServerSettings s)
    {
        var join = s.PublicAddress?.Trim();
        if (string.IsNullOrEmpty(join)) return null;
        var host = join.Contains(':') ? join[..join.LastIndexOf(':')] : join;
        var port = Uri.TryCreate(DefaultHostUrl, UriKind.Absolute, out var u) ? u.Port : 5080;
        return $"http://{host}:{port}";
    }

    /// <summary>
    /// Rust only downloads a custom map when server/&lt;identity&gt;/&lt;Name&gt;_&lt;hash of the URL&gt;.map is missing.
    /// Copying the library file there first means the server never downloads its own map, so it starts even when
    /// the download address only works from outside the network. Players still download it from the URL.
    /// </summary>
    public static string? PlaceForServer(ServerSettings s)
    {
        if (s.MapMode != MapMode.CustomMap || string.IsNullOrWhiteSpace(s.CustomMapFile) || string.IsNullOrWhiteSpace(s.InstallDir)) return null;
        var map = Find(s.CustomMapFile.Trim());
        if (map is null) return $"Custom map {s.CustomMapFile} isn't in the map library any more. Pick another map in Settings → World.";
        try
        {
            var folder = Path.Combine(s.InstallDir, "server", s.Identity);
            Directory.CreateDirectory(folder);
            var url = LevelUrl(s);
            var target = Path.Combine(folder, $"{RustWorldName(url)}_{MurmurHash(url)}.map");
            var src = new FileInfo(map.FullPath);
            var dst = new FileInfo(target);
            if (!dst.Exists || dst.Length != src.Length || dst.LastWriteTimeUtc < src.LastWriteTimeUtc)
                File.Copy(src.FullName, target, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't copy the custom map into the server folder ({ex.Message}). Rust will download it from {LevelUrl(s)} instead.";
        }
    }

    /// <summary>World.Name for a custom map: the URL's file name without extension.</summary>
    public static string RustWorldName(string url) => Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(url.Split('?')[0]));

    /// <summary>Facepunch's MurmurHash3 (seed 1337) of the UTF-8 string, as used for World.MapFileName.</summary>
    public static uint MurmurHash(string str)
    {
        var data = Encoding.UTF8.GetBytes(str ?? "");
        uint h = 1337, k;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            k = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            k *= 3432918353u; k = Rot(k, 15); k *= 461845907u;
            h ^= k; h = Rot(h, 13); h = h * 5 + 3864292196u;
        }
        var rest = data.Length - i;
        if (rest > 0)
        {
            k = rest switch
            {
                3 => (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16),
                2 => (uint)(data[i] | data[i + 1] << 8),
                _ => data[i],
            };
            k *= 3432918353u; k = Rot(k, 15); k *= 461845907u;
            h ^= k;
        }
        h ^= (uint)data.Length;
        h ^= h >> 16; h *= 2246822507u; h ^= h >> 13; h *= 3266489909u; h ^= h >> 16;
        return h;
    }

    private static uint Rot(uint x, int r) => x << r | x >> (32 - r);

    private static CustomMapInfo Describe(FileInfo f, bool bundled)
    {
        var image = new[] { ".jpg", ".png" }.Select(e => Path.ChangeExtension(f.FullName, e)).FirstOrDefault(File.Exists);
        var info = new CustomMapInfo(f.Name, f.FullName, f.Length, bundled, Path.GetFileNameWithoutExtension(f.Name), "", 0, image);
        var d = Details(info);
        return d is null ? info : info with { Title = string.IsNullOrWhiteSpace(d.Name) ? info.Title : d.Name, Description = d.Description, WorldSize = d.WorldSize };
    }
}
