using System.Text.RegularExpressions;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed record PermUser(string SteamId, string Name);

public sealed class GroupDetail
{
    public string Name { get; init; } = "";
    public List<PermUser> Users { get; } = new();
    public HashSet<string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UserPerms
{
    public string SteamId { get; init; } = "";
    public string Name { get; set; } = "";
    public HashSet<string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record BanEntry(string SteamId, string Name, string Reason, string Expiry);
public sealed record StaffEntry(string SteamId, string Name, string Role);

/// <summary>
/// Oxide/Carbon users, groups and permissions plus Rust bans and admin roles — all through the
/// server's own console commands, so it works for local and remote servers alike.
/// </summary>
public sealed partial class PermissionService(ServerConnectionManager conn)
{
    private static string P(ServerInstance s) => PluginService.Prefix(s.Settings);

    private async Task<string> Q(ServerInstance s, string cmd) =>
        await conn.QueryAsync(s.Id, cmd, TimeSpan.FromSeconds(8)) ?? throw new InvalidOperationException("The server isn't connected.");

    private async Task<string?> Do(ServerInstance s, string cmd)
    {
        if (!conn.Get(s.Id).IsOnline) return "The server isn't connected.";
        var reply = await conn.ExecuteAsync(s.Id, cmd);
        return Regex.IsMatch(reply, @"\b(not found|doesn't exist|does not exist|invalid|unknown|error)\b", RegexOptions.IgnoreCase) ? reply.Trim() : null;
    }

    private static string Arg(string v) => "\"" + v.Replace("\"", "'") + "\"";

    /// <summary>Split "Header:\na, b, c" style replies into items.</summary>
    private static IEnumerable<string> Items(string text) => text
        .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(i => i.Length > 0 && !i.EndsWith(':') && !i.StartsWith("No ", StringComparison.OrdinalIgnoreCase) && !i.Equals("none", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cut a reply into its "Something:" sections.</summary>
    private static Dictionary<string, string> Sections(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var buf = new System.Text.StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimEnd().EndsWith(':') && !line.Contains(','))
            {
                if (current is not null) result[current] = buf.ToString();
                current = line.Trim().TrimEnd(':');
                buf.Clear();
            }
            else buf.AppendLine(line);
        }
        if (current is not null) result[current] = buf.ToString();
        return result;
    }

    private static string? Section(Dictionary<string, string> sections, string keyword) =>
        sections.FirstOrDefault(kv => kv.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)).Value;

    private static IEnumerable<PermUser> ParseUsers(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var item in Items(text))
        {
            var id = SteamIdRegex().Match(item);
            if (!id.Success) continue;
            var name = item.Replace(id.Value, "").Trim(' ', '(', ')', '-', '"');
            yield return new PermUser(id.Value, name);
        }
    }

    // ---------------------------------------------------------------- reading

    public async Task<List<string>> GroupsAsync(ServerInstance s) =>
        Items(await Q(s, $"{P(s)}show groups")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();

    public async Task<List<string>> AllPermissionsAsync(ServerInstance s) =>
        Items(await Q(s, $"{P(s)}show perms")).Where(p => p.Contains('.')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();

    public async Task<GroupDetail> GroupAsync(ServerInstance s, string group)
    {
        var sections = Sections(await Q(s, $"{P(s)}show group {group}"));
        var g = new GroupDetail { Name = group };
        g.Users.AddRange(ParseUsers(Section(sections, "users") ?? Section(sections, "players")));
        foreach (var perm in Items(Section(sections, "permissions") ?? "")) g.Permissions.Add(perm);
        return g;
    }

    public async Task<UserPerms> UserAsync(ServerInstance s, string steamId)
    {
        var reply = await Q(s, $"{P(s)}show user {steamId}");
        var u = new UserPerms { SteamId = steamId };
        var name = Regex.Match(reply, @"User '([^']*)'");
        if (name.Success) u.Name = name.Groups[1].Value;
        var sections = Sections(reply);
        foreach (var g in Items(Section(sections, "groups") ?? "")) u.Groups.Add(g);
        foreach (var p in Items(Section(sections, "permissions") ?? "")) u.Permissions.Add(p);
        return u;
    }

    // ---------------------------------------------------------------- changing

    public Task<string?> CreateGroupAsync(ServerInstance s, string name, string title) =>
        Do(s, $"{P(s)}group add {Safe(name)} {Arg(string.IsNullOrWhiteSpace(title) ? name : title)}");
    public Task<string?> DeleteGroupAsync(ServerInstance s, string name) => Do(s, $"{P(s)}group remove {Safe(name)}");
    public Task<string?> AddToGroupAsync(ServerInstance s, string user, string group) => Do(s, $"{P(s)}usergroup add {Safe(user)} {Safe(group)}");
    public Task<string?> RemoveFromGroupAsync(ServerInstance s, string user, string group) => Do(s, $"{P(s)}usergroup remove {Safe(user)} {Safe(group)}");

    /// <summary>kind is "group" or "user".</summary>
    public Task<string?> SetPermissionAsync(ServerInstance s, string kind, string target, string perm, bool grant) =>
        Do(s, $"{P(s)}{(grant ? "grant" : "revoke")} {(kind == "user" ? "user" : "group")} {Safe(target)} {Safe(perm)}");

    private static string Safe(string v)
    {
        v = v.Trim();
        if (v.Length == 0 || v.Any(char.IsWhiteSpace) || v.Contains('"') || v.Contains(';'))
            throw new ArgumentException($"“{v}” can't contain spaces, quotes or semicolons.");
        return v;
    }

    // ---------------------------------------------------------------- bans & staff (vanilla Rust)

    public async Task<List<BanEntry>> BansAsync(ServerInstance s)
    {
        var reply = await Q(s, "banlistex");
        return BanRegex().Matches(reply)
            .Select(m => new BanEntry(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Success ? m.Groups[4].Value : ""))
            .ToList();
    }

    /// <summary>Ban by Steam ID (works whether or not they're online). Duration like "3d" or empty for permanent.</summary>
    public async Task<string?> BanAsync(ServerInstance s, string steamId, string name, string reason, string? duration)
    {
        if (!SteamIdRegex().IsMatch(steamId)) return "Enter a 17-digit Steam ID.";
        var cmd = $"banid {steamId} {Arg(string.IsNullOrWhiteSpace(name) ? steamId : name)} {Arg(string.IsNullOrWhiteSpace(reason) ? "Banned" : reason)}";
        if (!string.IsNullOrWhiteSpace(duration)) cmd += " " + Safe(duration);
        var err = await Do(s, cmd);
        if (err is null)
        {
            await conn.QueryAsync(s.Id, $"kick {steamId} {Arg(string.IsNullOrWhiteSpace(reason) ? "Banned" : reason)}");
            await conn.QueryAsync(s.Id, "server.writecfg");
        }
        return err;
    }

    public async Task<string?> UnbanAsync(ServerInstance s, string steamId)
    {
        var err = await Do(s, $"unban {Safe(steamId)}");
        if (err is null) await conn.QueryAsync(s.Id, "server.writecfg");
        return err;
    }

    public Task<string?> MuteAsync(ServerInstance s, string steamId, bool mute) => Do(s, $"{(mute ? "mutechat" : "unmutechat")} {Safe(steamId)}");

    /// <summary>role: "moderator", "owner" or "none".</summary>
    public async Task<string?> SetRoleAsync(ServerInstance s, string steamId, string name, string role)
    {
        string? err;
        if (role == "none")
        {
            await Do(s, $"removemoderator {Safe(steamId)}");
            err = await Do(s, $"removeowner {Safe(steamId)}");
        }
        else
        {
            err = await Do(s, $"{(role == "owner" ? "ownerid" : "moderatorid")} {Safe(steamId)} {Arg(name)} {Arg("Set from Rust Sandbox")}");
        }
        if (err is null) await conn.QueryAsync(s.Id, "server.writecfg");
        return err;
    }

    /// <summary>Owners/moderators from cfg/users.cfg — only readable when the server runs on this machine.</summary>
    public static List<StaffEntry>? ReadStaff(ServerInstance s)
    {
        if (!s.Settings.ManageProcess || string.IsNullOrWhiteSpace(s.Settings.InstallDir)) return null;
        var file = Path.Combine(s.Settings.InstallDir, "server", s.Settings.Identity, "cfg", "users.cfg");
        if (!File.Exists(file)) return new List<StaffEntry>();
        return StaffRegex().Matches(File.ReadAllText(file))
            .Select(m => new StaffEntry(m.Groups[2].Value, m.Groups[3].Value, m.Groups[1].Value.StartsWith("owner") ? "Owner" : "Moderator"))
            .ToList();
    }

    [GeneratedRegex(@"\b7656\d{13}\b")]
    private static partial Regex SteamIdRegex();

    [GeneratedRegex(@"(7656\d{13})\s+""([^""]*)""\s+""([^""]*)""(?:\s+(-?\d+))?")]
    private static partial Regex BanRegex();

    [GeneratedRegex(@"^\s*(ownerid|moderatorid)\s+(\d{17})\s+""([^""]*)""", RegexOptions.Multiline)]
    private static partial Regex StaffRegex();
}
