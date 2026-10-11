using System.Reflection;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>
/// A plugin shipped inside the manager that needs no settings from it, so installing just copies
/// the file into the server's plugins folder and reloads it.
/// </summary>
public sealed record BundledPlugin(string PluginName, string Label, string DownloadPath)
{
    /// <summary>In-game HUD showing running events (airdrop, heli, Bradley, cargo, Chinook, oil rigs), locked crate timers and a radiation detector.</summary>
    public static readonly BundledPlugin EventHud = new("RustSandboxEventHud", "Event HUD", "/api/plugin/eventhud");

    /// <summary>Skills, levels and perks (Mining, Woodcutting, Combat) with a solo-friendly XP bonus.</summary>
    public static readonly BundledPlugin Skills = new("RustSandboxSkills", "Skills plugin", "/api/plugin/skills");

    /// <summary>A daily spin for a random item with rarity tiers, streak bonuses and a spinning reel.</summary>
    public static readonly BundledPlugin Daily = new("RustSandboxDaily", "Daily rewards plugin", "/api/plugin/daily");

    /// <summary>Server events, starting with a treasure hunt across the map's monuments.</summary>
    public static readonly BundledPlugin Events = new("RustSandboxEvents", "Events plugin", "/api/plugin/events");

    /// <summary>Quest givers placed in the world, quests with gather/kill/loot/craft/deliver/keycard/visit goals and chosen rewards.</summary>
    public static readonly BundledPlugin Quests = new("RustSandboxQuests", "Quests plugin", "/api/plugin/quests");
    public static readonly BundledPlugin Help = new("RustSandboxHelp", "Help plugin", "/api/plugin/help");

    /// <summary>One deployable item that places a recycler with a repair bench built in.</summary>
    public static readonly BundledPlugin Recycler = new("RustSandboxRecycler", "Repair Recycler plugin", "/api/plugin/recycler");

    /// <summary>A /skins picker that puts any approved Rust skin on players' clothes, weapons and items.</summary>
    public static readonly BundledPlugin Skins = new("RustSandboxSkins", "Skins plugin", "/api/plugin/skins");

    /// <summary>Custom loot tables per crate type, with Rust's own loot as the default.</summary>
    public static readonly BundledPlugin Loot = new("RustSandboxLoot", "Loot plugin", "/api/plugin/loot");

    public string FileName => PluginName + ".cs";

    public string ReadSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException($"{Label} resource missing from build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string? CannotInstallReason(ServerInstance s)
    {
        if (s.IsDemo) return "Demo servers are simulated — plugins can't be installed here.";
        if (s.Settings.Framework == ModFramework.Vanilla) return $"The {Label} needs Oxide or Carbon. Pick one in Settings → Hosting and install it.";
        if (PluginService.Paths(s) is null) return "The manager needs to run this server on this machine (Settings → Hosting) to install the plugin. Download it instead.";
        return null;
    }

    public bool IsInstalled(ServerInstance s) =>
        PluginService.Paths(s) is { } p && File.Exists(Path.Combine(p.Plugins, FileName));
}

/// <summary>Installs <see cref="BundledPlugin"/>s on servers this machine runs.</summary>
public sealed class BundledPluginService(ServerConnectionManager conn, ILogger<BundledPluginService> log)
{
    /// <summary>Copy the plugin into the server's plugins folder and reload it. Returns an error or null.</summary>
    public async Task<string?> InstallAsync(ServerInstance s, BundledPlugin plugin)
    {
        if (plugin.CannotInstallReason(s) is { } reason) return reason;
        var paths = PluginService.Paths(s)!;
        try
        {
            Directory.CreateDirectory(paths.Plugins);
            await File.WriteAllTextAsync(Path.Combine(paths.Plugins, plugin.FileName), plugin.ReadSource());
            conn.AppendConsole(s.Id, ConsoleKind.System, $"{plugin.Label} installed to {paths.Plugins}.");
            if (conn.Get(s.Id).IsOnline)
            {
                // A new .cs file is hot-loaded by the framework; give it a moment, then reload to pick up an update.
                await Task.Delay(TimeSpan.FromSeconds(3));
                await conn.QueryAsync(s.Id, $"{paths.Prefix}reload {plugin.PluginName}", TimeSpan.FromSeconds(15));
            }
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Installing {Plugin} failed for server {Id}", plugin.PluginName, s.Id);
            return $"Couldn't install the plugin: {ex.Message}";
        }
    }
}
