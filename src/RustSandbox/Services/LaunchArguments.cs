using System.Globalization;
using System.Text;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>Turns <see cref="ServerSettings"/> into a RustDedicated command line.</summary>
public static class LaunchArguments
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "RustDedicated.exe" : "RustDedicated";

    public static string ExecutablePath(ServerSettings s) =>
        string.IsNullOrWhiteSpace(s.InstallDir) ? ExecutableName : Path.Combine(s.InstallDir, ExecutableName);

    public static List<(string Key, string Value)> Convars(ServerSettings s)
    {
        var list = new List<(string, string)>
        {
            ("server.ip", s.ServerIp),
            ("server.port", s.Port.ToString(CultureInfo.InvariantCulture)),
            ("server.queryport", s.QueryPort.ToString(CultureInfo.InvariantCulture)),
            ("rcon.ip", "0.0.0.0"),
            ("rcon.port", s.RconPort.ToString(CultureInfo.InvariantCulture)),
            ("rcon.password", s.RconPassword),
            ("rcon.web", "1"),
            ("app.port", s.AppPort.ToString(CultureInfo.InvariantCulture)),
            ("server.identity", s.Identity),
            ("server.hostname", s.EffectiveHostname),
            ("server.maxplayers", s.MaxPlayers.ToString(CultureInfo.InvariantCulture)),
        };

        switch (s.MapMode)
        {
            case MapMode.Procedural:
                list.Add(("server.level", "Procedural Map"));
                list.Add(("server.worldsize", s.WorldSize.ToString(CultureInfo.InvariantCulture)));
                list.Add(("server.seed", s.Seed.ToString(CultureInfo.InvariantCulture)));
                // The file is written into server/<identity> before each start (see WorldOptions.WriteFile).
                if (s.World is { Enabled: true }) list.Add(("world.configfile", WorldOptions.FileName));
                break;
            case MapMode.CustomMap:
                list.Add(("server.levelurl", CustomMaps.LevelUrl(s)));
                break;
            default:
                list.Add(("server.level", s.MapMode switch
                {
                    MapMode.HapisIsland => "HapisIsland",
                    MapMode.CraggyIsland => "CraggyIsland",
                    MapMode.SavasIsland => "SavasIsland",
                    _ => "Barren",
                }));
                if (s.MapMode == MapMode.Barren)
                {
                    list.Add(("server.worldsize", s.WorldSize.ToString(CultureInfo.InvariantCulture)));
                    list.Add(("server.seed", s.Seed.ToString(CultureInfo.InvariantCulture)));
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(s.Description)) list.Add(("server.description", s.Description.Replace("\r", "").Replace("\n", "\\n")));
        if (!string.IsNullOrWhiteSpace(s.Url)) list.Add(("server.url", s.Url));
        if (!string.IsNullOrWhiteSpace(s.HeaderImage)) list.Add(("server.headerimage", s.HeaderImage));
        if (!string.IsNullOrWhiteSpace(s.LogoImage)) list.Add(("server.logoimage", s.LogoImage));
        if (!string.IsNullOrWhiteSpace(s.Tags)) list.Add(("server.tags", s.Tags));

        list.Add(("server.pve", s.Pve ? "true" : "false"));
        list.Add(("server.radiation", s.Radiation ? "true" : "false"));
        list.Add(("server.stability", s.Stability ? "true" : "false"));
        list.Add(("decay.scale", s.DecayScale.ToString("0.##", CultureInfo.InvariantCulture)));
        list.Add(("server.saveinterval", s.SaveInterval.ToString(CultureInfo.InvariantCulture)));
        list.Add(("server.tickrate", s.TickRate.ToString(CultureInfo.InvariantCulture)));
        list.Add(("fps.limit", s.FpsLimit.ToString(CultureInfo.InvariantCulture)));
        list.Add(("server.secure", s.Secure ? "true" : "false"));

        foreach (var c in s.CustomConvars.Where(c => !string.IsNullOrWhiteSpace(c.Key)))
        {
            // A custom value overrides a built-in one with the same name.
            list.RemoveAll(x => string.Equals(x.Item1, c.Key.Trim(), StringComparison.OrdinalIgnoreCase));
            list.Add((c.Key.Trim(), c.Value));
        }
        return list;
    }

    public static string Build(ServerSettings s, bool maskPassword = false)
    {
        var sb = new StringBuilder("-batchmode -nographics");
        foreach (var (key, value) in Convars(s))
        {
            var v = maskPassword && key == "rcon.password" && value.Length > 0 ? "********" : value;
            sb.Append(" +").Append(key).Append(' ').Append(Quote(v));
        }
        if (!string.IsNullOrWhiteSpace(s.ExtraArgs)) sb.Append(' ').Append(s.ExtraArgs.Trim());
        return sb.ToString();
    }

    public static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':' or '/' or ','))
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
