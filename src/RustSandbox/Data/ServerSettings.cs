using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustSandbox.Data;

public enum MapMode { Procedural, CustomMap, Barren, HapisIsland, CraggyIsland, SavasIsland }
public enum ModFramework { Vanilla, Oxide, Carbon }

public class ConvarEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Everything needed to launch, connect to and monitor one Rust server.</summary>
public class ServerSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---- General ----
    [Required, MaxLength(128)] public string Hostname { get; set; } = "My Rust Server";
    [Required, RegularExpression(@"^[A-Za-z0-9_\-]+$", ErrorMessage = "Letters, numbers, - and _ only")]
    public string Identity { get; set; } = "server1";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public string LogoImage { get; set; } = "";
    public string Tags { get; set; } = "monthly,vanilla";
    [Range(1, 1000)] public int MaxPlayers { get; set; } = 100;

    // ---- Network ----
    public string ServerIp { get; set; } = "0.0.0.0";
    [Range(1, 65535)] public int Port { get; set; } = 28015;
    [Range(1, 65535)] public int QueryPort { get; set; } = 28017;
    /// <summary>Address players join with (public IP or domain). Empty = use the RCON host when it isn't local.</summary>
    public string PublicAddress { get; set; } = "";
    /// <summary>Host the manager uses to reach WebRCON.</summary>
    public string RconHost { get; set; } = "127.0.0.1";
    [Range(1, 65535)] public int RconPort { get; set; } = 28016;
    public string RconPassword { get; set; } = "";
    /// <summary>Rust+ companion app port. -1 disables.</summary>
    [Range(-1, 65535)] public int AppPort { get; set; } = 28082;

    // ---- World ----
    public MapMode MapMode { get; set; } = MapMode.Procedural;
    [Range(0, int.MaxValue)] public int Seed { get; set; } = 1337;
    [Range(1000, 6000)] public int WorldSize { get; set; } = 4000;
    public string LevelUrl { get; set; } = "";
    /// <summary>A map from the manager's map library (e.g. "Maui.map"), hosted by the manager. Empty = use LevelUrl.</summary>
    public string CustomMapFile { get; set; } = "";
    /// <summary>Address players download hosted maps from, e.g. http://203.0.113.5:5080. Empty = the manager's Public URL.</summary>
    public string MapHostUrl { get; set; } = "";
    /// <summary>Optional image (e.g. exported from RustMaps) used as the live map background.</summary>
    public string MapImageUrl { get; set; } = "";
    /// <summary>True when MapImageUrl was picked up automatically from the server log (replaced on new maps).</summary>
    public bool MapImageAuto { get; set; }
    /// <summary>Ocean border around the island in the map image, in metres per side. Facepunch's images measured about 1000 on a 4000 map.</summary>
    [Range(0, 3000)] public int MapImageMarginMeters { get; set; } = 1000;
    /// <summary>Which map image MapImageMarginMeters was hand-tuned for. Any other image is aligned automatically.</summary>
    public string MapImageMarginSource { get; set; } = "";

    /// <summary>Rust's world generation settings (biomes, roads, monuments...), used for procedural maps.</summary>
    public WorldOptions World { get; set; } = new();

    // ---- Rates & stacks (RustSandboxTweaks plugin) ----
    public GameTweaks Tweaks { get; set; } = new();

    /// <summary>Hostname as sent to the game, with the gather rate tag when that option is on.</summary>
    [JsonIgnore]
    public string EffectiveHostname =>
        Tweaks is { Enabled: true, ShowRateInHostname: true } t && Math.Abs(t.GatherRate - 1) > 0.001 && !Hostname.Contains($"[{t.RateLabel}]")
            ? $"{Hostname} [{t.RateLabel}]"
            : Hostname;

    // ---- Gameplay ----
    public bool Pve { get; set; }
    public bool Radiation { get; set; } = true;
    public bool Stability { get; set; } = true;
    [Range(0, 10)] public double DecayScale { get; set; } = 1.0;
    [Range(10, 3600)] public int SaveInterval { get; set; } = 600;
    [Range(10, 128)] public int TickRate { get; set; } = 30;
    [Range(-1, 1000)] public int FpsLimit { get; set; } = 256;
    public bool Secure { get; set; } = true;

    // ---- Runtime / hosting ----
    /// <summary>When true the manager starts/stops RustDedicated on this machine.</summary>
    public bool ManageProcess { get; set; }
    public string InstallDir { get; set; } = "";
    public string SteamCmdPath { get; set; } = "";
    /// <summary>Optional: the Rust game (client) folder, e.g. D:\Steam\steamapps\common\Rust. Its Bundles\items folder has item icons and details.</summary>
    public string RustClientDir { get; set; } = "";
    /// <summary>Steam beta branch, empty for public.</summary>
    public string Branch { get; set; } = "";
    public ModFramework Framework { get; set; } = ModFramework.Oxide;
    public bool AutoRestartOnCrash { get; set; } = true;
    public bool AutoStartWithManager { get; set; }
    public string ExtraArgs { get; set; } = "";
    /// <summary>Apply Rust/Oxide updates automatically when the checker finds them.</summary>
    public bool AutoUpdate { get; set; }
    [Range(0, 60)] public int UpdateWarnMinutes { get; set; } = 10;
    public List<ConvarEntry> CustomConvars { get; set; } = new();

    // ---- Notifications ----
    public string DiscordWebhookUrl { get; set; } = "";
    public bool NotifyStatusChanges { get; set; } = true;
    public bool NotifyLongKills { get; set; }
    [Range(50, 2000)] public int LongKillMeters { get; set; } = 250;

    // ---- Telemetry ----
    [Range(2, 60)] public int PositionIntervalSeconds { get; set; } = 5;

    public ServerSettings Clone() =>
        JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>"host:port" players connect to, or null when unknown.</summary>
    public string? ConnectAddress
    {
        get
        {
            var host = !string.IsNullOrWhiteSpace(PublicAddress) ? PublicAddress.Trim()
                : RconHost is "127.0.0.1" or "localhost" or "" ? null : RconHost;
            if (host is null) return null;
            // Steam and the Rust client want a numeric IP, not "localhost".
            if (host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1" + host[9..];
            return host.Contains(':') ? host : $"{host}:{Port}";
        }
    }

    /// <summary>Steam link that launches Rust and joins the server.</summary>
    public string? SteamConnectUrl => ConnectAddress is { } a ? $"steam://connect/{a}" : null;

    public string RustMapsUrl => $"https://rustmaps.com/map/{WorldSize}_{Seed}";

    public string MapDisplayName => MapMode switch
    {
        MapMode.Procedural => $"Procedural {WorldSize} · seed {Seed}",
        MapMode.CustomMap => string.IsNullOrWhiteSpace(CustomMapFile) ? "Custom map" : $"{Path.GetFileNameWithoutExtension(CustomMapFile)} {WorldSize}",
        _ => MapMode.ToString(),
    };
}
