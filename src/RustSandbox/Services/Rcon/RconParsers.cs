using System.Globalization;
using System.Text.Json;

namespace RustSandbox.Services.Rcon;

/// <summary>Lenient parsers for Rust's JSON command replies (field types vary between versions).</summary>
public static class RconParsers
{
    public static ServerInfo? ParseServerInfo(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new ServerInfo
            {
                Hostname = Str(r, "Hostname"),
                MaxPlayers = (int)Num(r, "MaxPlayers"),
                Players = (int)Num(r, "Players"),
                Queued = (int)Num(r, "Queued"),
                Joining = (int)Num(r, "Joining"),
                EntityCount = (int)Num(r, "EntityCount"),
                GameTime = Str(r, "GameTime"),
                Uptime = (int)Num(r, "Uptime"),
                Map = Str(r, "Map"),
                Framerate = Num(r, "Framerate"),
                Memory = (int)Num(r, "Memory"),
                NetworkIn = (long)Num(r, "NetworkIn"),
                NetworkOut = (long)Num(r, "NetworkOut"),
                Version = Str(r, "Version"),
                Protocol = Str(r, "Protocol"),
                SaveCreatedTime = Str(r, "SaveCreatedTime"),
                Restarting = Bool(r, "Restarting"),
            };
        }
        catch (JsonException) { return null; }
    }

    public static List<OnlinePlayer>? ParsePlayerList(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('[')) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<OnlinePlayer>();
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                list.Add(new OnlinePlayer
                {
                    SteamId = Str(p, "SteamID"),
                    Name = Str(p, "DisplayName"),
                    Ping = (int)Num(p, "Ping"),
                    Address = Str(p, "Address"),
                    ConnectedSeconds = (int)Num(p, "ConnectedSeconds"),
                    Health = Num(p, "Health"),
                    // Facepunch spells it "VoiationLevel"; accept both.
                    ViolationLevel = Has(p, "ViolationLevel") ? Num(p, "ViolationLevel") : Num(p, "VoiationLevel"),
                });
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Chat frames carry a JSON payload: {"Channel":0,"Message":"hi","UserId":"7656...","Username":"Bob",...}</summary>
    public static ChatMessage? ParseChat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new ChatMessage(DateTime.UtcNow, Str(r, "UserId"), Str(r, "Username"), Str(r, "Message"), (int)Num(r, "Channel"));
        }
        catch (JsonException) { return null; }
    }

    private static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);

    public static string Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText(),
        };
    }

    public static double Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return 0;
    }

    public static bool Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True ||
               (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }
}
