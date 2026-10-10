using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>
/// The project used to be called Rust Server Manager. These helpers carry an existing install over:
/// its data folder and database file, and the old plugin files on servers this machine runs.
/// </summary>
public static class LegacyRename
{
    public const string OldName = "RustServerManager";
    private const string OldDatabase = "rustservermanager.db";

    /// <summary>Move C:\ProgramData\RustServerManager to the new data folder if that doesn't exist yet.</summary>
    public static void MoveDataFolder(string newDir)
    {
        var parent = Path.GetDirectoryName(newDir.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null) return;
        var oldDir = Path.Combine(parent, OldName);
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return;
        try { Directory.Move(oldDir, newDir); }
        catch (IOException) { /* in use or on another drive: start fresh rather than fail */ }
    }

    /// <summary>Rename rustservermanager.db (and its -wal/-shm files) next to the new database path.</summary>
    public static void MoveDatabase(string newPath)
    {
        var dir = Path.GetDirectoryName(newPath);
        if (dir is null || File.Exists(newPath)) return;
        var oldPath = Path.Combine(dir, OldDatabase);
        if (!File.Exists(oldPath) || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        File.Move(oldPath, newPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(oldPath + suffix)) File.Move(oldPath + suffix, newPath + suffix, overwrite: true);
    }
}

/// <summary>
/// Swaps the old RustServerManager* plugins for the RustSandbox* ones on servers this machine runs.
/// The old file is deleted first so the plugin unloads and saves; the new plugin then copies its
/// settings and player data over the first time it loads.
/// </summary>
public sealed class LegacyPluginMigration(ServerService servers, ServerConnectionManager conn, ILogger<LegacyPluginMigration> log) : BackgroundService
{
    private static readonly (string Old, string New, Func<string> Source)[] Plugins =
    {
        (LegacyRename.OldName + "Telemetry", "RustSandboxTelemetry", ProcessManager.ReadPluginSource),
        (LegacyRename.OldName + "Tweaks", TweaksService.PluginName, TweaksService.ReadPluginSource),
        (LegacyRename.OldName + "EventHud", BundledPlugin.EventHud.PluginName, () => BundledPlugin.EventHud.ReadSource()),
        (LegacyRename.OldName + "Skills", BundledPlugin.Skills.PluginName, () => BundledPlugin.Skills.ReadSource()),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give RCON a moment to connect so running servers unload the old plugins cleanly.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        foreach (var s in await servers.GetAllAsync())
        {
            if (PluginService.Paths(s) is not { } paths) continue;
            foreach (var (oldName, newName, source) in Plugins)
            {
                foreach (var folder in new[] { paths.Plugins, paths.Disabled })
                {
                    var oldFile = Path.Combine(folder, oldName + ".cs");
                    if (!File.Exists(oldFile)) continue;
                    try
                    {
                        File.Delete(oldFile);
                        if (folder == paths.Plugins && conn.Get(s.Id).IsOnline)
                            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                        var newFile = Path.Combine(folder, newName + ".cs");
                        if (!File.Exists(newFile)) await File.WriteAllTextAsync(newFile, source(), stoppingToken);
                        conn.AppendConsole(s.Id, ConsoleKind.System, $"Renamed plugin {oldName} to {newName}. Its settings and player data carry over.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Couldn't rename plugin {Plugin} on server {Id}", oldName, s.Id);
                    }
                }
            }
        }
    }
}
