using System.Text.Json;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>The RustSandboxWeather plugin's config file (its field names).</summary>
public sealed class WeatherConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Vanilla, MostlyClear, Varied, Stormy or Custom.</summary>
    public string Mix { get; set; } = "Varied";
    public Dictionary<string, float> Custom { get; set; } = WeatherService.MixOf("Varied");
    public bool Announce { get; set; } = true;
    public int ForceMinutes { get; set; } = 20;
}

/// <summary>What the plugin says about the weather right now (sandboxweather.status).</summary>
public sealed class WeatherStatus
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("mix")] public string Mix { get; set; } = "";
    [JsonPropertyName("previous")] public string? Previous { get; set; }
    [JsonPropertyName("current")] public string? Current { get; set; }
    [JsonPropertyName("next")] public string? Next { get; set; }
    [JsonPropertyName("blend")] public double Blend { get; set; }
    [JsonPropertyName("nextChangeMinutes")] public double NextChangeMinutes { get; set; }
    [JsonPropertyName("forced")] public string? Forced { get; set; }
    [JsonPropertyName("forcedMinutesLeft")] public double ForcedMinutesLeft { get; set; }
    [JsonPropertyName("rainGrace")] public bool RainGrace { get; set; }
    [JsonPropertyName("chances")] public Dictionary<string, double> Chances { get; set; } = new();

    public bool Changing => Blend < 0.99 && Previous != Current;
}

/// <summary>Talks to the RustSandboxWeather plugin: settings in its config file, live weather over RCON.</summary>
public sealed class WeatherService(ServerConnectionManager conn, ILogger<WeatherService> log)
{
    public static BundledPlugin Plugin => BundledPlugin.Weather;

    public static readonly string[] Kinds = { "Clear", "Overcast", "Fog", "Rain", "Storm", "Dust" };

    public static readonly (string Id, string Name, string About)[] Mixes =
    {
        ("MostlyClear", "Mostly clear", "Sunny most of the time, with the odd shower, fog or storm."),
        ("Varied", "Varied", "A good mix: clear about 40% of the time, the rest rain, fog, overcast, storms and dust."),
        ("Stormy", "Stormy", "Rain and storms half the time."),
        ("Vanilla", "Rust's own", "Exactly what Rust does without the plugin."),
        ("Custom", "Custom", "Pick how often each kind of weather comes up."),
    };

    public static Dictionary<string, float> MixOf(string id) => id switch
    {
        "MostlyClear" => Make(70, 12, 5, 8, 3, 2),
        "Stormy" => Make(15, 20, 10, 30, 20, 5),
        _ => Make(40, 18, 12, 15, 8, 7),
    };

    private static Dictionary<string, float> Make(float clear, float overcast, float fog, float rain, float storm, float dust) =>
        new() { ["Clear"] = clear, ["Overcast"] = overcast, ["Fog"] = fog, ["Rain"] = rain, ["Storm"] = storm, ["Dust"] = dust };

    public static string Label(string? kind) => kind switch
    {
        "Clear" => "Clear",
        "Overcast" => "Overcast",
        "Fog" => "Fog",
        "Rain" => "Rain",
        "Storm" => "Storm",
        "Dust" => "Dust haze",
        null or "" => "–",
        _ => kind,
    };

    public static string Emoji(string? kind) => kind switch
    {
        "Clear" => "☀️",
        "Overcast" => "☁️",
        "Fog" => "🌫️",
        "Rain" => "🌧️",
        "Storm" => "⛈️",
        "Dust" => "🌪️",
        _ => "",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    public static string? ConfigPath(ServerInstance s) =>
        PluginService.Paths(s) is { } p ? Path.Combine(p.Config, Plugin.PluginName + ".json") : null;

    public static async Task<(WeatherConfig? Config, string? Error)> ReadConfigAsync(ServerInstance s)
    {
        var path = ConfigPath(s);
        if (path is null) return (null, "Weather settings live in the plugin's config file, so the manager needs to run this server on this machine (Settings → Hosting).");
        if (!File.Exists(path)) return (new WeatherConfig(), null);
        try
        {
            var c = JsonSerializer.Deserialize<WeatherConfig>(await File.ReadAllTextAsync(path), Lenient) ?? new WeatherConfig();
            foreach (var k in Kinds) c.Custom.TryAdd(k, 0);
            return (c, null);
        }
        catch (Exception ex) { return (null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>Write the config (keeping a .bak) and reload the plugin. Returns an error or null.</summary>
    public async Task<string?> SaveConfigAsync(ServerInstance s, WeatherConfig config)
    {
        var path = ConfigPath(s);
        if (path is null) return "This server isn't run by this machine.";
        try
        {
            foreach (var k in Kinds) config.Custom[k] = Math.Clamp((float)Math.Round(config.Custom.GetValueOrDefault(k), 1), 0, 100);
            config.ForceMinutes = Math.Clamp(config.ForceMinutes, 1, 600);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, Indented));
            if (conn.Get(s.Id).IsOnline)
                await conn.QueryAsync(s.Id, $"{PluginService.Prefix(s.Settings)}reload {Plugin.PluginName}", TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving the weather config failed for server {Id}", s.Id);
            return $"Couldn't save: {ex.Message}";
        }
    }

    public async Task<WeatherStatus?> StatusAsync(int serverId)
    {
        if (!conn.Get(serverId).IsOnline) return null;
        var reply = await conn.QueryAsync(serverId, "sandboxweather.status", TimeSpan.FromSeconds(10));
        var start = reply?.IndexOf('{') ?? -1;
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<WeatherStatus>(reply![start..]); }
        catch (JsonException ex) { log.LogDebug(ex, "Couldn't read the weather plugin's status"); return null; }
    }

    /// <summary>Set the weather now (a kind from <see cref="Kinds"/>) or "random" to go back to random weather.</summary>
    public Task<string?> NowAsync(int serverId, string kind, int minutes) =>
        conn.QueryAsync(serverId, $"sandboxweather.now {kind} {minutes}", TimeSpan.FromSeconds(10));
}
