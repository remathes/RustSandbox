namespace RustSandbox.Services;

public enum ToastKind { Info, Success, Warning, Error }

public sealed record Toast(Guid Id, string Message, ToastKind Kind);

/// <summary>Per-circuit toast notifications.</summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = new();
    public IReadOnlyList<Toast> Toasts => _toasts;
    public event Action? Changed;

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        _toasts.Add(toast);
        Changed?.Invoke();
        _ = DismissLaterAsync(toast.Id, kind == ToastKind.Error ? 8000 : 4500);
    }

    public void Success(string m) => Show(m, ToastKind.Success);
    public void Error(string m) => Show(m, ToastKind.Error);
    public void Warn(string m) => Show(m, ToastKind.Warning);

    /// <summary>Show either the error or the success message from a "null means OK" result.</summary>
    public void Result(string? error, string success)
    {
        if (error is null) Success(success);
        else Error(error);
    }

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }

    private async Task DismissLaterAsync(Guid id, int ms)
    {
        await Task.Delay(ms);
        Dismiss(id);
    }
}

/// <summary>Which server the person is looking at, so the shell (sidebar, top bar) can follow the page.</summary>
public sealed class ServerContext
{
    public int? ServerId { get; private set; }
    public event Action? Changed;

    public void Set(int? id)
    {
        if (ServerId == id) return;
        ServerId = id;
        Changed?.Invoke();
    }
}

/// <summary>Time windows used across stats pages.</summary>
public static class StatRanges
{
    public static readonly (string Key, string Label)[] All =
    {
        ("24h", "24 hours"), ("7d", "7 days"), ("30d", "30 days"), ("wipe", "This wipe"), ("all", "All time"),
    };

    public static DateTime? From(string key, DateTime? lastWipeUtc) => key switch
    {
        "1h" => DateTime.UtcNow.AddHours(-1),
        "6h" => DateTime.UtcNow.AddHours(-6),
        "24h" => DateTime.UtcNow.AddHours(-24),
        "7d" => DateTime.UtcNow.AddDays(-7),
        "30d" => DateTime.UtcNow.AddDays(-30),
        "wipe" => lastWipeUtc,
        _ => null,
    };
}

public static class Fmt
{
    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{Math.Max(0, t.Seconds)}s";

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }

    public static string Num(double n) => n >= 1_000_000 ? $"{n / 1_000_000:0.#}M" : n >= 10_000 ? $"{n / 1000:0.#}k" : n.ToString("N0");

    public static string Local(DateTime utc, string format = "MMM d, HH:mm") =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString(format);

    /// <summary>Wrap a console argument in quotes, swapping any inner double quotes for single ones.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\"", "'") + "\"";

    public static string SteamProfile(string steamId) => $"https://steamcommunity.com/profiles/{steamId}";

    /// <summary>Rust map grid reference (e.g. "G12") for a world position. Cells are ~146.3 m.</summary>
    public static string Grid(float x, float z, int worldSize)
    {
        const float cell = 146.28571f;
        var col = (int)Math.Floor((x + worldSize / 2f) / cell);
        var row = (int)Math.Floor((worldSize / 2f - z) / cell);
        if (col < 0 || row < 0) return "—";
        return ColumnName(col) + row;
    }

    public static string ColumnName(int col)
    {
        var name = "";
        col++;
        while (col > 0)
        {
            var rem = (col - 1) % 26;
            name = (char)('A' + rem) + name;
            col = (col - 1) / 26;
        }
        return name;
    }
}
