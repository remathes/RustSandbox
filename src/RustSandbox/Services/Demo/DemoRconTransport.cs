using System.Text;
using System.Text.Json;
using RustSandbox.Services.Rcon;

namespace RustSandbox.Services.Demo;

/// <summary>Answers RCON commands from the simulated world so the console behaves like a real server.</summary>
public sealed class DemoRconTransport(DemoWorld world, int serverId) : IRconTransport
{
    public int ServerId { get; } = serverId;
    private bool _connected;

    public bool IsConnected => _connected;
    public event Action<RconMessage>? MessageReceived;
#pragma warning disable CS0067 // never raised: demo connections don't drop
    public event Action<string>? Disconnected;
#pragma warning restore CS0067

    public Task ConnectAsync(CancellationToken ct)
    {
        _connected = true;
        world.Register(this);
        return Task.CompletedTask;
    }

    internal void Push(string type, string message) => MessageReceived?.Invoke(new RconMessage(0, type, message));

    public async Task<string> SendAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_connected) throw new InvalidOperationException("Not connected");
        await Task.Delay(Random.Shared.Next(15, 70), ct);
        var state = world.GetState(ServerId);
        var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var arg = parts.Length > 1 ? parts[1] : "";
        if (verb.StartsWith("global.")) verb = verb[7..];

        lock (state.Lock)
        {
            var online = state.Players.Where(p => p.Online).ToList();
            switch (verb)
            {
                case "serverinfo":
                    var uptime = (int)(DateTime.UtcNow - state.StartedUtc).TotalSeconds;
                    return JsonSerializer.Serialize(new
                    {
                        Hostname = "Demo Server | Monthly | Vanilla+",
                        MaxPlayers = 100,
                        Players = online.Count,
                        Queued = online.Count > 55 ? Random.Shared.Next(0, 4) : 0,
                        Joining = Random.Shared.Next(0, 3),
                        EntityCount = state.Entities,
                        GameTime = DateTime.UtcNow.AddHours(3).ToString("MM/dd/yyyy HH:mm:ss"),
                        Uptime = uptime,
                        Map = "Procedure Map",
                        Framerate = Math.Round(Math.Clamp(62 - online.Count * 0.18 + Random.Shared.NextDouble() * 8 - 4, 18, 90)),
                        Memory = 6200 + state.Entities / 60 + Random.Shared.Next(0, 300),
                        Collections = 120,
                        NetworkIn = 40_000 + online.Count * 2_200 + Random.Shared.Next(0, 8000),
                        NetworkOut = 120_000 + online.Count * 9_000 + Random.Shared.Next(0, 20000),
                        Restarting = false,
                        SaveCreatedTime = state.StartedUtc.AddDays(-3).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        Version = 2590,
                        Protocol = "2590.262.1",
                    });

                case "playerlist":
                    return JsonSerializer.Serialize(online.Select(p => new
                    {
                        SteamID = p.SteamId,
                        OwnerSteamID = "0",
                        DisplayName = p.Name,
                        Ping = p.Ping + Random.Shared.Next(-5, 6),
                        Address = $"10.0.{p.Ping % 255}.{p.Name.Length * 7 % 255}:{50000 + p.Ping}",
                        ConnectedSeconds = (int)(DateTime.UtcNow - p.OnlineSinceUtc).TotalSeconds,
                        VoiationLevel = 0.0,
                        CurrentLevel = 0.0,
                        UnspentXp = 0.0,
                        Health = Math.Round(p.Health, 1),
                    }));

                case "status":
                    var sb = new StringBuilder();
                    sb.AppendLine("hostname: Demo Server | Monthly | Vanilla+");
                    sb.AppendLine("version : 2590/262 secure (secure mode enabled, connected to Steam3)");
                    sb.AppendLine("map     : Procedure Map");
                    sb.AppendLine($"players : {online.Count} (100 max) (0 queued) (0 joining)");
                    sb.AppendLine();
                    sb.AppendLine("id                name                     ping connected addr");
                    foreach (var p in online.Take(40))
                        sb.AppendLine($"{p.SteamId} {('"' + p.Name + '"'),-24} {p.Ping,4} {(int)(DateTime.UtcNow - p.OnlineSinceUtc).TotalSeconds,9}s");
                    return sb.ToString();

                case "say":
                    var text = arg.Trim('"');
                    Push("Chat", JsonSerializer.Serialize(new { Channel = 0, Message = text, UserId = "0", Username = "SERVER", Color = "#eee", Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
                    return "";

                case "kick":
                case "ban":
                    var target = arg.Split(' ', 2)[0].Trim('"');
                    var victim = state.Players.FirstOrDefault(p => p.Online && (p.SteamId == target || p.Name.Equals(target, StringComparison.OrdinalIgnoreCase)));
                    if (victim is null) return $"Player '{target}' not found";
                    victim.Online = false;
                    Push("Generic", $"{victim.SteamId}/{victim.Name} {(verb == "kick" ? "kicked" : "banned")}");
                    return $"{(verb == "kick" ? "Kicked" : "Banned")} {victim.Name}";

                case "server.save":
                case "save":
                    Push("Generic", "Saving complete");
                    return "Saved " + state.Entities.ToString("N0") + " ents, cache(0.07), write(0.02), disk(0.01).";

                case "server.writecfg":
                case "writecfg":
                    return "Config Saved";

                case "oxide.plugins":
                case "o.plugins":
                case "plugins":
                    return DemoPluginList();

                case "env.time":
                    return arg.Length > 0 ? $"env.time: \"{arg}\"" : "env.time: \"13.42\"";

                case "restart":
                    Push("Generic", $"[ServerVar] Restarting in {arg} (demo — nothing will actually restart)");
                    return "";

                case "quit":
                    return "Demo servers can't be shut down — they are simulated.";

                case "oxide.show":
                case "oxide.group":
                case "oxide.usergroup":
                case "oxide.grant":
                case "oxide.revoke":
                case "c.show":
                case "c.group":
                case "c.usergroup":
                case "c.grant":
                case "c.revoke":
                    return Permissions(state, verb[(verb.IndexOf('.') + 1)..], arg);

                case "banlistex":
                    return string.Join("\n", state.Bans.Select((b, i) => $"{i + 1} {b.Id} \"{b.Name}\" \"{b.Reason}\" -1"));

                case "banid":
                    var parts2 = arg.Split(' ', 3);
                    state.Bans.Add((parts2[0], parts2.Length > 1 ? parts2[1].Trim('"') : parts2[0], parts2.Length > 2 ? parts2[2].Trim('"') : ""));
                    return $"Banned {parts2[0]}";

                case "unban":
                    var removed = state.Bans.RemoveAll(b => b.Id == arg.Trim());
                    return removed > 0 ? $"Unbanned User: {arg.Trim()}" : $"User {arg.Trim()} not found";

                case "help":
                case "find":
                    return "Common commands: status, serverinfo, playerlist, say <msg>, kick <id|name> <reason>, ban <id|name>, server.save, server.writecfg, env.time <0-24>, weather.load <Clear|Storm>, oxide.plugins, oxide.reload <name>, inventory.giveto <id> <item> <amount>, restart <seconds> <reason>";

                default:
                    return $"[demo] '{command}' executed.";
            }
        }
    }

    private static string Permissions(DemoServerState state, string sub, string arg)
    {
        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('"')).ToArray();
        string Name(string id) => state.Players.FirstOrDefault(p => p.SteamId == id)?.Name ?? id;
        switch (sub)
        {
            case "show" when a.Length > 0 && a[0] == "groups":
                return "Groups:\n" + string.Join(", ", state.Groups.Keys);
            case "show" when a.Length > 0 && a[0] == "perms":
                return "Permissions:\n" + string.Join(", ", DemoServerState.AllPerms);
            case "show" when a.Length > 1 && a[0] == "group":
                if (!state.Groups.TryGetValue(a[1], out var g)) return $"Group '{a[1]}' doesn't exist";
                return $"Group '{a[1]}' users:\n{(g.Users.Count == 0 ? "No users" : string.Join(", ", g.Users.Select(u => $"{u} ({Name(u)})")))}\nGroup '{a[1]}' permissions:\n{(g.Perms.Count == 0 ? "No permissions" : string.Join(", ", g.Perms))}";
            case "show" when a.Length > 1 && a[0] == "user":
                var groups = state.Groups.Where(kv => kv.Key == "default" || kv.Value.Users.Contains(a[1])).Select(kv => kv.Key);
                var perms = state.UserPerms.TryGetValue(a[1], out var up) ? up : new HashSet<string>();
                return $"User '{Name(a[1])}' permissions:\n{(perms.Count == 0 ? "No permissions" : string.Join(", ", perms))}\nUser '{Name(a[1])}' groups:\n{string.Join(", ", groups)}";
            case "group" when a.Length > 1 && a[0] == "add":
                state.Groups.TryAdd(a[1], (new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                return $"Group '{a[1]}' created";
            case "group" when a.Length > 1 && a[0] == "remove":
                return state.Groups.Remove(a[1]) ? $"Group '{a[1]}' deleted" : $"Group '{a[1]}' doesn't exist";
            case "usergroup" when a.Length > 2:
                if (!state.Groups.TryGetValue(a[2], out var ug)) return $"Group '{a[2]}' doesn't exist";
                if (a[0] == "add") ug.Users.Add(a[1]); else ug.Users.Remove(a[1]);
                return $"User '{Name(a[1])}' {(a[0] == "add" ? "added to" : "removed from")} group: {a[2]}";
            case "grant" or "revoke" when a.Length > 2:
                HashSet<string> set;
                if (a[0] == "group")
                {
                    if (!state.Groups.TryGetValue(a[1], out var gg)) return $"Group '{a[1]}' doesn't exist";
                    set = gg.Perms;
                }
                else
                {
                    if (!state.UserPerms.TryGetValue(a[1], out var existing))
                    {
                        existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        state.UserPerms[a[1]] = existing;
                    }
                    set = existing;
                }
                if (sub == "grant") set.Add(a[2]); else set.Remove(a[2]);
                return $"{(sub == "grant" ? "Granted" : "Revoked")} '{a[2]}' {(sub == "grant" ? "to" : "from")} {a[0]} '{a[1]}'";
            default:
                return "Syntax: oxide.show groups | perms | group <name> | user <id>";
        }
    }

    private static readonly DateTime DemoPluginsLoaded = DateTime.UtcNow.AddMinutes(-90);

    // Hook totals grow with uptime (seconds of hook time per second of server time) so the Plugins page's
    // Performance tab has live numbers to show.
    private static string DemoPluginList()
    {
        var up = (DateTime.UtcNow - DemoPluginsLoaded).TotalSeconds;
        var rows = new (string Title, string Ver, string Author, double Rate, string Mem, string File)[]
        {
            ("Rust Sandbox Telemetry", "1.2.0", "RustSandbox", 0.0094, "1.8 MB", "RustSandboxTelemetry"),
            ("Better Chat", "5.2.11", "LaserHydra", 0.0018, "412 KB", "BetterChat"),
            ("Gather Manager", "2.2.78", "Mughisi", 0.0041, "96 KB", "GatherManager"),
            ("Kits", "4.4.2", "k1lly0u", 0.0009, "2.4 MB", "Kits"),
            ("NTeleportation", "1.8.4", "nivex", 0.0032, "3.1 MB", "NTeleportation"),
            ("Remover Tool", "4.3.41", "Tryhard", 0.0014, "640 KB", "RemoverTool"),
        };
        var sb = new System.Text.StringBuilder($"Listing {rows.Length} plugins:");
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            sb.Append(FormattableString.Invariant($"\n  {i + 1:00} \"{r.Title}\" ({r.Ver}) by {r.Author} ({r.Rate * up:0.000}s / {r.Mem}) - {r.File}.cs"));
        }
        return sb.ToString();
    }

    public ValueTask DisposeAsync()
    {
        if (_connected)
        {
            _connected = false;
            world.Unregister(this);
        }
        return ValueTask.CompletedTask;
    }
}
