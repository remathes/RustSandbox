using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustSandbox.Services;

public sealed class HuntStage
{
    [JsonPropertyName("monument")] public string Monument { get; set; } = "";
    [JsonPropertyName("grid")] public string Grid { get; set; } = "";
}

public sealed class HuntHunter
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("stage")] public int Stage { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
}

public sealed class HuntStatus
{
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("startGrid")] public string StartGrid { get; set; } = "";
    [JsonPropertyName("minutesLeft")] public double MinutesLeft { get; set; }
    [JsonPropertyName("statueSpawned")] public bool StatueSpawned { get; set; }
    [JsonPropertyName("stages")] public List<HuntStage> Stages { get; set; } = new();
    [JsonPropertyName("hunters")] public List<HuntHunter> Hunters { get; set; } = new();
}

public sealed class HeistStatus
{
    [JsonPropertyName("grid")] public string Grid { get; set; } = "";
    [JsonPropertyName("minutesLeft")] public double MinutesLeft { get; set; }
    [JsonPropertyName("guards")] public int Guards { get; set; }
    [JsonPropertyName("hacking")] public bool Hacking { get; set; }
    [JsonPropertyName("hacker")] public string? Hacker { get; set; }
    [JsonPropertyName("won")] public bool Won { get; set; }
}

public sealed class EventsStatus
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("hunt")] public HuntStatus? Hunt { get; set; }
    [JsonPropertyName("heist")] public HeistStatus? Heist { get; set; }
}

/// <summary>Starts, stops and watches events in the RustSandboxEvents plugin over RCON.</summary>
public sealed class EventsService(ServerConnectionManager conn)
{
    public static BundledPlugin Plugin => BundledPlugin.Events;

    /// <summary>The console command a schedule runs to start a treasure hunt.</summary>
    public static string HuntCommand(int clues, double minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"sandboxevents.hunt.start {clues} {minutes}");

    public async Task<EventsStatus?> StatusAsync(int serverId)
    {
        var reply = await conn.QueryAsync(serverId, "sandboxevents.status", TimeSpan.FromSeconds(8));
        var start = reply?.IndexOf('{') ?? -1;
        if (start < 0) return null;
        try { return JsonSerializer.Deserialize<EventsStatus>(reply![start..]); }
        catch (JsonException) { return null; }
    }

    public Task<string?> StartHuntAsync(int serverId, int clues, double minutes) =>
        conn.QueryAsync(serverId, HuntCommand(clues, minutes), TimeSpan.FromSeconds(10));

    public Task<string?> StopHuntAsync(int serverId) =>
        conn.QueryAsync(serverId, "sandboxevents.hunt.stop", TimeSpan.FromSeconds(10));

    /// <summary>The console command a schedule runs to start a train heist.</summary>
    public static string HeistCommand(int guards, double minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"sandboxevents.heist.start {guards} {minutes}");

    public Task<string?> StartHeistAsync(int serverId, int guards, double minutes) =>
        conn.QueryAsync(serverId, HeistCommand(guards, minutes), TimeSpan.FromSeconds(15));

    public Task<string?> StopHeistAsync(int serverId) =>
        conn.QueryAsync(serverId, "sandboxevents.heist.stop", TimeSpan.FromSeconds(10));
}
