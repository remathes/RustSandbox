using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace RustSandbox.Data;

/// <summary>A Rust server instance managed by this app. Most configuration lives in <see cref="ServerSettings"/>.</summary>
public class ServerInstance
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = "New Server";

    /// <summary>Accent colour used to tell servers apart in the switcher.</summary>
    [MaxLength(16)]
    public string Color { get; set; } = "#ce422b";

    /// <summary>Demo servers are simulated so the UI can be explored without a real Rust server.</summary>
    public bool IsDemo { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Shared secret the telemetry plugin sends in the X-Api-Key header.</summary>
    [MaxLength(64)]
    public string TelemetryApiKey { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastWipeUtc { get; set; }

    private string _settingsJson = "{}";
    private ServerSettings? _settings;

    public string SettingsJson
    {
        get => _settings is null ? _settingsJson : JsonSerializer.Serialize(_settings, ServerSettings.JsonOptions);
        set { _settingsJson = string.IsNullOrWhiteSpace(value) ? "{}" : value; _settings = null; }
    }

    [NotMapped]
    public ServerSettings Settings
    {
        get => _settings ??= JsonSerializer.Deserialize<ServerSettings>(_settingsJson, ServerSettings.JsonOptions) ?? new ServerSettings();
        set => _settings = value;
    }
}

public enum ActorType
{
    Unknown = 0,
    Player = 1,
    Scientist = 2,
    Animal = 3,
    Helicopter = 4,
    Bradley = 5,
    Turret = 6,
    Trap = 7,
    Environment = 8,
    Self = 9,
    Explosion = 10,
}

/// <summary>A death reported by the telemetry plugin (player deaths, and kills of NPCs/animals/vehicles by players).</summary>
public class KillEvent
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public DateTime Timestamp { get; set; }

    [MaxLength(32)] public string KillerId { get; set; } = "";
    [MaxLength(64)] public string KillerName { get; set; } = "";
    public ActorType KillerType { get; set; }

    [MaxLength(32)] public string VictimId { get; set; } = "";
    [MaxLength(64)] public string VictimName { get; set; } = "";
    public ActorType VictimType { get; set; }

    [MaxLength(64)] public string Weapon { get; set; } = "";
    /// <summary>Cause of death for non-weapon deaths (Fall, Cold, Radiation, Bleeding...).</summary>
    [MaxLength(32)] public string DamageType { get; set; } = "";
    [MaxLength(32)] public string BodyPart { get; set; } = "";
    public bool IsHeadshot { get; set; }
    public float Distance { get; set; }

    public float VictimX { get; set; }
    public float VictimZ { get; set; }
    public float KillerX { get; set; }
    public float KillerZ { get; set; }

    [NotMapped] public bool IsPvp => KillerType == ActorType.Player && VictimType == ActorType.Player && KillerId != VictimId;
    [NotMapped] public bool IsPlayerDeath => VictimType == ActorType.Player;
}

/// <summary>Periodic player position sample used for the heat map.</summary>
public class PositionSample
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    [MaxLength(32)] public string SteamId { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ConnectionEvent
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    [MaxLength(32)] public string SteamId { get; set; } = "";
    [MaxLength(64)] public string Name { get; set; } = "";
    public bool IsConnect { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>Shots fired / hits landed per player, weapon and day. Drives "most used weapon" and accuracy.</summary>
public class WeaponUsage
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    [MaxLength(32)] public string SteamId { get; set; } = "";
    [MaxLength(64)] public string Weapon { get; set; } = "";
    public DateTime Day { get; set; }
    public int Shots { get; set; }
    public int Hits { get; set; }
}

/// <summary>What a player gathered per item, source and day (wood from trees, berries from farming...).</summary>
public class GatherStat
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    [MaxLength(32)] public string SteamId { get; set; } = "";
    /// <summary>Item display name, e.g. "Metal Ore".</summary>
    [MaxLength(64)] public string Item { get; set; } = "";
    /// <summary>Resource group from <see cref="Services.GatherCatalog"/>, e.g. "Metal" or "Berries".</summary>
    [MaxLength(24)] public string Category { get; set; } = "";
    /// <summary>tree, ore, animal, pickup, farm or fishing.</summary>
    [MaxLength(16)] public string Source { get; set; } = "";
    public DateTime Day { get; set; }
    public long Amount { get; set; }
    /// <summary>Number of hits/pickups/harvests that gave this item.</summary>
    public int Times { get; set; }
}

public class SavedSeed
{
    public int Id { get; set; }
    public int Seed { get; set; }
    public int WorldSize { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(500)] public string Notes { get; set; } = "";
    public bool IsFavorite { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum ScheduleKind
{
    Daily = 0,
    Weekly = 1,
    Interval = 2,
    /// <summary>First Thursday of the month — Facepunch's forced wipe day.</summary>
    FirstThursday = 3,
    Once = 4,
}

public enum ScheduleAction
{
    Restart = 0,
    Command = 1,
    Announce = 2,
    Save = 3,
    UpdateServer = 4,
    MapWipe = 5,
    FullWipe = 6,
}

public class ScheduledTask
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ScheduleKind Kind { get; set; }
    public ScheduleAction Action { get; set; }

    /// <summary>Minutes after local midnight for Daily/Weekly/FirstThursday.</summary>
    public int TimeOfDayMinutes { get; set; } = 6 * 60;
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Thursday;
    public int IntervalMinutes { get; set; } = 60;
    public DateTime? OnceAtUtc { get; set; }

    /// <summary>Command text for Command, message for Announce.</summary>
    [MaxLength(500)] public string Payload { get; set; } = "";
    /// <summary>In-game countdown before restarts/wipes.</summary>
    public int WarningMinutes { get; set; } = 5;
    /// <summary>For wipes: roll a fresh random seed.</summary>
    public bool NewSeedOnWipe { get; set; } = true;

    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    [MaxLength(200)] public string LastResult { get; set; } = "";
}

/// <summary>A one-click console command button. ServerId null means it shows on every server.</summary>
public class CommandMacro
{
    public int Id { get; set; }
    public int? ServerId { get; set; }
    [MaxLength(40)] public string Label { get; set; } = "";
    [MaxLength(500)] public string Command { get; set; } = "";
    public bool Confirm { get; set; }
    public int SortOrder { get; set; }
}

public class AppSetting
{
    [Key, MaxLength(64)] public string Key { get; set; } = "";
    [MaxLength(2000)] public string Value { get; set; } = "";
}
