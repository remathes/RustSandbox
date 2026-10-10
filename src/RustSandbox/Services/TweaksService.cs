using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>An item as reported by the tweaks plugin (vanilla values).</summary>
public sealed class RustItem
{
    [JsonPropertyName("s")] public string Short { get; set; } = "";
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("c")] public string Category { get; set; } = "";
    [JsonPropertyName("d")] public int DefaultStack { get; set; }
    [JsonPropertyName("now")] public int CurrentStack { get; set; }
    [JsonPropertyName("cookTo")] public string? CookTo { get; set; }
    [JsonPropertyName("cookTime")] public double CookTime { get; set; }
    [JsonPropertyName("cookAmount")] public double CookAmount { get; set; }

    // Filled from the Rust game folder (Bundles\items) when available; not cached.
    [JsonIgnore] public bool HasIcon { get; set; }
    [JsonIgnore] public string Description { get; set; } = "";
    [JsonIgnore] public string Rarity { get; set; } = "";
}

/// <summary>
/// Installs and drives the RustSandboxTweaks plugin: writes its config from the server's
/// GameTweaks settings and reloads it, and fetches the server's item list for the stack editor.
/// </summary>
public sealed class TweaksService(ServerConnectionManager conn, ServerService servers, ILogger<TweaksService> log)
{
    public const string PluginName = "RustSandboxTweaks";
    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = true };

    public static string ReadPluginSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PluginName + ".cs")
            ?? throw new InvalidOperationException("Tweaks plugin resource missing from build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string? CannotApplyReason(ServerInstance s)
    {
        if (s.IsDemo) return "Demo servers are simulated — rates and stacks can't be applied here.";
        if (s.Settings.Framework == ModFramework.Vanilla) return "Rates and stacks need Oxide or Carbon. Pick one in Settings → Hosting and install it.";
        if (PluginService.Paths(s) is null) return "The manager needs to run this server on this machine (Settings → Hosting) to install the plugin.";
        return null;
    }

    public static bool IsInstalled(ServerInstance s) =>
        PluginService.Paths(s) is { } p && File.Exists(Path.Combine(p.Plugins, PluginName + ".cs"));

    /// <summary>Write the config (and plugin if missing or outdated) and reload it. Returns an error or null.</summary>
    public async Task<string?> ApplyAsync(ServerInstance s)
    {
        if (CannotApplyReason(s) is { } reason) return reason;
        var paths = PluginService.Paths(s)!;
        try
        {
            Directory.CreateDirectory(paths.Plugins);
            Directory.CreateDirectory(paths.Config);
            await File.WriteAllTextAsync(Path.Combine(paths.Config, PluginName + ".json"), JsonSerializer.Serialize(s.Settings.Tweaks, ConfigJson));
            var hudChanged = await SetRadiationTintAsync(paths, s.Settings.Tweaks.RadiationTint);

            var pluginFile = Path.Combine(paths.Plugins, PluginName + ".cs");
            var source = ReadPluginSource();
            var fresh = !File.Exists(pluginFile) || await File.ReadAllTextAsync(pluginFile) != source;
            if (fresh)
            {
                await File.WriteAllTextAsync(pluginFile, source);
                conn.AppendConsole(s.Id, ConsoleKind.System, $"Rates & stacks plugin installed to {paths.Plugins}.");
            }

            if (conn.Get(s.Id).IsOnline)
            {
                // A new .cs file is hot-loaded by the framework; give it a moment, then reload so it reads the new config.
                if (fresh) await Task.Delay(TimeSpan.FromSeconds(3));
                await conn.QueryAsync(s.Id, $"{paths.Prefix}reload {PluginName}", TimeSpan.FromSeconds(15));
                if (hudChanged) await conn.QueryAsync(s.Id, $"{paths.Prefix}reload {BundledPlugin.EventHud.PluginName}", TimeSpan.FromSeconds(15));
                await conn.QueryAsync(s.Id, $"server.hostname {Fmt.Quote(s.Settings.EffectiveHostname)}", TimeSpan.FromSeconds(5));
                conn.AppendConsole(s.Id, ConsoleKind.System, "Rates & stacks applied.");
            }
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Applying tweaks failed for server {Id}", s.Id);
            return $"Couldn't apply: {ex.Message}";
        }
    }

    private const string TintKey = "Tint the screen edges in radiation";

    /// <summary>
    /// The radiation screen tint is drawn by the Event HUD plugin, so it's switched in that plugin's config.
    /// Returns true when the setting changed (the HUD then needs a reload). Skipped when the HUD isn't installed.
    /// </summary>
    private static async Task<bool> SetRadiationTintAsync(PluginPaths paths, bool on)
    {
        var file = Path.Combine(paths.Config, BundledPlugin.EventHud.PluginName + ".json");
        if (!File.Exists(file)) return false;
        if (System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file)) is not System.Text.Json.Nodes.JsonObject cfg) return false;
        if (cfg[TintKey] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var was) && was == on) return false;
        cfg[TintKey] = on;
        await File.WriteAllTextAsync(file, cfg.ToJsonString(ConfigJson));
        return true;
    }

    /// <summary>"applied", "off", "config unreadable", or null when the plugin isn't loaded / server offline.</summary>
    public async Task<string?> StatusAsync(ServerInstance s)
    {
        if (!conn.Get(s.Id).IsOnline) return null;
        var reply = await conn.QueryAsync(s.Id, "sandboxtweaks.status", TimeSpan.FromSeconds(5));
        if (reply is null) return null;
        reply = reply.Trim();
        return reply is "applied" or "off" or "config unreadable" ? reply : null;
    }

    /// <summary>
    /// The server's item list with vanilla stack sizes. Asks the plugin when the server is online
    /// (and caches the answer), otherwise uses the last cached list.
    /// </summary>
    public async Task<(List<RustItem> Items, string? Error)> GetItemsAsync(ServerInstance s, bool refresh = false)
    {
        if (s.IsDemo) return (new(), "Demo servers don't have a real item list.");
        var catalog = ItemCatalog.Load(s.Settings);
        var (items, error) = await ServerItemsAsync(s, refresh);

        if (items.Count == 0 && catalog.Count > 0)
        {
            // No answer from the plugin yet: the game folder has every item's vanilla stack size too.
            items = catalog.Values.Select(c => new RustItem { Short = c.Short, Name = c.Name, Category = c.Category, DefaultStack = c.Stack, CurrentStack = c.Stack })
                .OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
            error = null;
        }
        foreach (var i in items)
        {
            if (!catalog.TryGetValue(i.Short, out var c)) continue;
            i.HasIcon = c.HasIcon;
            i.Description = c.Description;
            i.Rarity = c.Rarity;
        }
        return (items, error);
    }

    /// <summary>The live item list from the plugin (cached), or empty with a reason.</summary>
    private async Task<(List<RustItem> Items, string? Error)> ServerItemsAsync(ServerInstance s, bool refresh)
    {
        var key = $"items:{s.Id}";
        if (!refresh && await servers.GetAppSettingAsync(key) is { Length: > 2 } cached)
        {
            var list = Parse(cached);
            if (list.Count > 0) return (list, null);
        }

        if (!conn.Get(s.Id).IsOnline)
            return (new(), "Start the server once with the plugin installed to load the item list, or set the Rust game folder in Settings → Hosting.");

        var reply = await conn.QueryAsync(s.Id, "sandboxtweaks.items", TimeSpan.FromSeconds(20));
        var items = reply is null ? new List<RustItem>() : Parse(reply);
        if (items.Count == 0)
            return (new(), IsInstalled(s)
                ? "The plugin didn't answer. Check the console for compile errors, then refresh."
                : "Press Apply once to install the plugin, then refresh the item list.");

        await servers.SetAppSettingAsync(key, JsonSerializer.Serialize(items));
        return (items, null);
    }

    private static List<RustItem> Parse(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0) return new();
        try
        {
            return (JsonSerializer.Deserialize<List<RustItem>>(json[start..]) ?? new())
                .Where(i => !string.IsNullOrEmpty(i.Short))
                .OrderBy(i => i.Category).ThenBy(i => i.Name)
                .ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
