using System.Text.Json;
using System.Text.RegularExpressions;
using RustSandbox.Data;
using RustSandbox.Services.Rcon;

namespace RustSandbox.Services;

public sealed class PluginInfo
{
    /// <summary>File / class name without .cs — what Oxide's commands use.</summary>
    public string Name { get; init; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Loaded { get; set; }
    public bool HasConfig { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    /// <summary>Newer version found on uMod, if any.</summary>
    public UmodPlugin? Update { get; set; }
}

public sealed class UmodPlugin
{
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Url { get; init; } = "";
    public string Downloads { get; init; } = "";
}

public sealed record PluginPaths(string Plugins, string Disabled, string Config, string Prefix);

/// <summary>
/// Manages Oxide/Carbon plugins. Full control (files, configs, installs) needs the server on this
/// machine; for remote servers it falls back to what RCON allows: list, load, unload, reload.
/// </summary>
public sealed partial class PluginService(ServerConnectionManager conn, IHttpClientFactory http, ILogger<PluginService> log)
{
    public static string Prefix(ServerSettings s) => s.Framework == ModFramework.Carbon ? "c." : "oxide.";

    public static PluginPaths? Paths(ServerInstance s)
    {
        if (s.IsDemo || !s.Settings.ManageProcess || string.IsNullOrWhiteSpace(s.Settings.InstallDir)) return null;
        var root = s.Settings.InstallDir;
        return s.Settings.Framework switch
        {
            ModFramework.Oxide => new PluginPaths(Path.Combine(root, "oxide", "plugins"), Path.Combine(root, "oxide", "plugins", "disabled"), Path.Combine(root, "oxide", "config"), "oxide."),
            ModFramework.Carbon => new PluginPaths(Path.Combine(root, "carbon", "plugins"), Path.Combine(root, "carbon", "plugins", "disabled"), Path.Combine(root, "carbon", "configs"), "c."),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- listing

    public async Task<List<PluginInfo>> ListAsync(ServerInstance s)
    {
        var loaded = await LoadedAsync(s);
        var result = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        var paths = Paths(s);

        if (paths is not null)
        {
            foreach (var (dir, enabled) in new[] { (paths.Plugins, true), (paths.Disabled, false) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.cs"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var info = ParseHeader(await File.ReadAllTextAsync(file), name);
                    info.Enabled = enabled;
                    info.ModifiedUtc = File.GetLastWriteTimeUtc(file);
                    info.HasConfig = File.Exists(Path.Combine(paths.Config, name + ".json"));
                    result[name] = info;
                }
            }
        }

        foreach (var l in loaded)
        {
            if (result.TryGetValue(l.Name, out var existing))
            {
                existing.Loaded = true;
                if (!string.IsNullOrEmpty(l.Version)) existing.Version = l.Version;
            }
            else if (paths is null)
            {
                l.Enabled = true;
                result[l.Name] = l;
            }
        }
        return result.Values.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Plugins the server reports as loaded (oxide.plugins / c.plugins).</summary>
    private async Task<List<PluginInfo>> LoadedAsync(ServerInstance s)
    {
        var list = new List<PluginInfo>();
        if (s.Settings.Framework == ModFramework.Vanilla && !s.IsDemo) return list;
        var reply = await conn.QueryAsync(s.Id, Prefix(s.Settings) + "plugins", TimeSpan.FromSeconds(8));
        if (string.IsNullOrEmpty(reply)) return list;
        foreach (Match m in LoadedLineRegex().Matches(reply))
        {
            list.Add(new PluginInfo
            {
                Name = m.Groups["file"].Value,
                Title = m.Groups["title"].Value,
                Version = m.Groups["ver"].Value,
                Author = m.Groups["author"].Value.Trim(),
                Loaded = true,
            });
        }
        // Fallback for list formats we don't recognise (other Oxide/Carbon builds): any "Name.cs" in the reply is loaded.
        foreach (var line in reply.Split('\n'))
        {
            // Plugins that failed to compile or load can be listed too — those aren't loaded.
            if (line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unloaded", StringComparison.OrdinalIgnoreCase) || line.Contains("not loaded", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Match m in FileNameRegex().Matches(line))
            {
                var name = m.Groups[1].Value;
                if (!list.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new PluginInfo { Name = name, Title = name, Loaded = true });
            }
        }
        return list;
    }

    private static PluginInfo ParseHeader(string source, string name)
    {
        var info = new PluginInfo { Name = name, Title = name };
        var m = InfoRegex().Match(source);
        if (m.Success)
        {
            info.Title = m.Groups[1].Value;
            info.Author = m.Groups[2].Value;
            info.Version = m.Groups[3].Value;
        }
        var d = DescriptionRegex().Match(source);
        if (d.Success) info.Description = d.Groups[1].Value;
        return info;
    }

    // ---------------------------------------------------------------- actions

    private static string? NeedsFiles(PluginPaths? p) => p is null
        ? "Plugin files are only reachable when the manager runs this server (Settings → Hosting) with Oxide or Carbon."
        : null;

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            throw new ArgumentException("Invalid plugin name.");
        return name;
    }

    public async Task<string?> RunAsync(ServerInstance s, string verb, string name)
    {
        if (!conn.Get(s.Id).IsOnline) return "The server isn't connected, so the plugin can't be " + verb + "ed right now.";
        var reply = await conn.ExecuteAsync(s.Id, $"{Prefix(s.Settings)}{verb} {SafeName(name)}");
        return reply.Contains("error", StringComparison.OrdinalIgnoreCase) || reply.Contains("failed", StringComparison.OrdinalIgnoreCase) ? reply : null;
    }

    public async Task<string?> SetEnabledAsync(ServerInstance s, string name, bool enable)
    {
        name = SafeName(name);
        var p = Paths(s);
        if (p is null)
        {
            // Remote: only (un)load in memory — it comes back after a restart.
            return await RunAsync(s, enable ? "load" : "unload", name);
        }
        var from = Path.Combine(enable ? p.Disabled : p.Plugins, name + ".cs");
        var to = Path.Combine(enable ? p.Plugins : p.Disabled, name + ".cs");
        if (!File.Exists(from)) return $"{name}.cs not found.";
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: true);
        if (conn.Get(s.Id).IsOnline) await conn.ExecuteAsync(s.Id, $"{p.Prefix}{(enable ? "load" : "unload")} {name}");
        return null;
    }

    public async Task<string?> DeleteAsync(ServerInstance s, string name, bool deleteConfig)
    {
        name = SafeName(name);
        var p = Paths(s);
        var err = NeedsFiles(p);
        if (err is not null) return err;
        if (conn.Get(s.Id).IsOnline) await conn.QueryAsync(s.Id, $"{p!.Prefix}unload {name}");
        foreach (var f in new[] { Path.Combine(p!.Plugins, name + ".cs"), Path.Combine(p.Disabled, name + ".cs") })
            if (File.Exists(f)) File.Delete(f);
        if (deleteConfig)
        {
            var cfg = Path.Combine(p.Config, name + ".json");
            if (File.Exists(cfg)) File.Delete(cfg);
        }
        return null;
    }

    public async Task<(string? Json, string? Error)> ReadConfigAsync(ServerInstance s, string name)
    {
        var p = Paths(s);
        var err = NeedsFiles(p);
        if (err is not null) return (null, err);
        var file = Path.Combine(p!.Config, SafeName(name) + ".json");
        if (!File.Exists(file)) return (null, $"{name} has no config file yet. Most plugins create one the first time they load.");
        return (await File.ReadAllTextAsync(file), null);
    }

    /// <summary>Validate, back up the old file, save and reload the plugin.</summary>
    public async Task<string?> SaveConfigAsync(ServerInstance s, string name, string json)
    {
        var p = Paths(s);
        var err = NeedsFiles(p);
        if (err is not null) return err;
        try
        {
            using var _ = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            return $"That isn't valid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}). Nothing was saved.";
        }
        var file = Path.Combine(p!.Config, SafeName(name) + ".json");
        Directory.CreateDirectory(p.Config);
        if (File.Exists(file)) File.Copy(file, file + ".bak", overwrite: true);
        await File.WriteAllTextAsync(file, json);
        if (conn.Get(s.Id).IsOnline) await conn.ExecuteAsync(s.Id, $"{p.Prefix}reload {name}");
        return null;
    }

    /// <summary>Install a plugin from its C# source (upload or download).</summary>
    public async Task<string?> InstallSourceAsync(ServerInstance s, string fileName, string source)
    {
        var p = Paths(s);
        var err = NeedsFiles(p);
        if (err is not null) return err;
        var name = SafeName(Path.GetFileNameWithoutExtension(fileName));
        if (!source.Contains("namespace Oxide.Plugins") && !source.Contains("namespace Carbon.Plugins"))
            return "That doesn't look like an Oxide/Carbon plugin (no Oxide.Plugins namespace).";
        Directory.CreateDirectory(p!.Plugins);
        var disabled = Path.Combine(p.Disabled, name + ".cs");
        if (File.Exists(disabled)) File.Delete(disabled);
        await File.WriteAllTextAsync(Path.Combine(p.Plugins, name + ".cs"), source);
        // Oxide and Carbon watch the folder and compile the file automatically.
        if (conn.Get(s.Id).IsOnline) await conn.QueryAsync(s.Id, $"{p.Prefix}load {name}");
        return null;
    }

    public async Task<string?> InstallFromUmodAsync(ServerInstance s, UmodPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.DownloadUrl)) return "uMod didn't give a download link for this plugin.";
        try
        {
            var source = await http.CreateClient("downloads").GetStringAsync(plugin.DownloadUrl);
            var fileName = Path.GetFileName(new Uri(plugin.DownloadUrl).AbsolutePath);
            if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) fileName = plugin.Name + ".cs";
            return await InstallSourceAsync(s, fileName, source);
        }
        catch (Exception ex)
        {
            return $"Download failed: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- uMod

    public async Task<List<UmodPlugin>> SearchUmodAsync(string query, CancellationToken ct = default)
    {
        var url = $"https://umod.org/plugins/search.json?query={Uri.EscapeDataString(query)}&page=1&sort=downloads&sortdir=desc&categories%5B%5D=rust";
        var json = await http.CreateClient("downloads").GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<UmodPlugin>();
        var data = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in data.EnumerateArray())
        {
            var name = RconParsers.Str(p, "name");
            var author = RconParsers.Str(p, "author");
            if (p.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object) author = RconParsers.Str(a, "name");
            var download = RconParsers.Str(p, "download_url");
            if (string.IsNullOrEmpty(download) && !string.IsNullOrEmpty(name)) download = $"https://umod.org/plugins/{name}.cs";
            list.Add(new UmodPlugin
            {
                Name = name,
                Title = FirstNonEmpty(RconParsers.Str(p, "title"), name),
                Author = author,
                Version = FirstNonEmpty(RconParsers.Str(p, "latest_release_version"), RconParsers.Str(p, "version")),
                Description = RconParsers.Str(p, "description"),
                DownloadUrl = download,
                Url = FirstNonEmpty(RconParsers.Str(p, "url"), $"https://umod.org/plugins/{name}"),
                Downloads = FirstNonEmpty(RconParsers.Str(p, "downloads_shortened"), RconParsers.Str(p, "downloads")),
            });
        }
        return list;
    }

    /// <summary>Look up each installed plugin on uMod and attach newer versions. Returns how many have updates.</summary>
    public async Task<int> CheckUpdatesAsync(IEnumerable<PluginInfo> installed, CancellationToken ct = default)
    {
        var count = 0;
        using var throttle = new SemaphoreSlim(4);
        var tasks = installed.Select(async p =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var hits = await SearchUmodAsync(p.Title, ct);
                var match = hits.FirstOrDefault(h => h.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                            ?? hits.FirstOrDefault(h => h.Title.Equals(p.Title, StringComparison.OrdinalIgnoreCase));
                p.Update = match is not null && UpdateService.IsNewer(match.Version, p.Version) ? match : null;
                if (p.Update is not null) Interlocked.Increment(ref count);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "uMod lookup failed for {Plugin}", p.Name);
            }
            finally { throttle.Release(); }
        });
        await Task.WhenAll(tasks);
        return count;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    [GeneratedRegex(@"\[Info\(\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""")]
    private static partial Regex InfoRegex();

    [GeneratedRegex(@"\[Description\(\s*""([^""]*)""")]
    private static partial Regex DescriptionRegex();

    // e.g.  01 "Better Chat" (5.2.11) by LaserHydra (0.02s) - BetterChat.cs
    // Oxide lines look like:  01 "Title" (1.2.3) by Author (0.12s / 1.4 MB) - FileName.cs
    // (older builds print just "(0.12s)", so the timing part is matched loosely).
    [GeneratedRegex(@"""(?<title>[^""]+)""\s*\((?<ver>[^)]*)\)\s*by\s*(?<author>.+?)\s*\([^)]*\)\s*-\s*(?<file>[\w\-]+)\.cs")]
    private static partial Regex LoadedLineRegex();

    [GeneratedRegex(@"\b([A-Za-z0-9_\-]+)\.cs\b")]
    private static partial Regex FileNameRegex();
}
