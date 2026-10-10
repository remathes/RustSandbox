using System.Collections.Concurrent;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>The picture used behind the live map.</summary>
/// <param name="Url">Where the browser loads it from.</param>
/// <param name="Key">Identifies this particular image, so a hand-tuned alignment only applies to the image it was made for.</param>
/// <param name="Rendered">True for a high-res map made on the server with world.rendermap.</param>
public sealed record MapImage(string Url, string Key, bool Rendered);

/// <summary>
/// Picks the live map background: a world.rendermap PNG in the install folder (sharpest) beats the image
/// Facepunch uploads at startup, which beats nothing (generated preview).
/// </summary>
public static class MapImages
{
    // Rust adds a 500 pixel ocean border on every side of its map renders, whatever the scale.
    private const int OceanMarginPixels = 500;

    private static readonly ConcurrentDictionary<int, (DateTime Checked, FileInfo? File)> Cache = new();

    public static string RenderedFilePattern(ServerInstance s) => $"map_{s.Settings.WorldSize}_{s.Settings.Seed}*.png";

    /// <summary>Newest map_&lt;size&gt;_&lt;seed&gt;.png in the server folder, if any. Cached for a few seconds.</summary>
    public static FileInfo? FindRendered(ServerInstance s, bool fresh = false)
    {
        if (s.IsDemo || string.IsNullOrWhiteSpace(s.Settings.InstallDir)) return null;
        if (!fresh && Cache.TryGetValue(s.Id, out var c) && DateTime.UtcNow - c.Checked < TimeSpan.FromSeconds(10)
            && (c.File is null || c.File.Name.StartsWith($"map_{s.Settings.WorldSize}_{s.Settings.Seed}", StringComparison.OrdinalIgnoreCase)))
            return c.File;
        FileInfo? file = null;
        try
        {
            if (Directory.Exists(s.Settings.InstallDir))
                file = new DirectoryInfo(s.Settings.InstallDir).GetFiles(RenderedFilePattern(s))
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        }
        catch { /* folder not readable */ }
        Cache[s.Id] = (DateTime.UtcNow, file);
        return file;
    }

    public static void Forget(int serverId) => Cache.TryRemove(serverId, out _);

    private static readonly SemaphoreSlim ThumbLock = new(1, 1);

    /// <summary>
    /// A small JPEG of the server's rendered map for the server list (the full render can be 20 MB).
    /// Made once per map file and cached; null when there's no render or this isn't Windows.
    /// </summary>
    public static async Task<string?> ThumbnailAsync(ServerInstance s, int size = 256)
    {
        var map = FindRendered(s);
        if (map is null || !OperatingSystem.IsWindows()) return null;
        var dir = Path.Combine(AppPaths.DataDir ?? Path.Combine(Path.GetTempPath(), "RustSandbox"), "map-thumbs");
        var thumb = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(map.Name)}_{map.LastWriteTimeUtc.Ticks:x}_{size}.jpg");
        if (File.Exists(thumb)) return thumb;
        await ThumbLock.WaitAsync();
        try
        {
            if (File.Exists(thumb)) return thumb;
            Directory.CreateDirectory(dir);
            await Task.Run(() => { if (OperatingSystem.IsWindows()) MakeThumbnail(map.FullName, thumb, size); });
            if (!File.Exists(thumb)) return null;
            return thumb;
        }
        catch
        {
            return null;
        }
        finally
        {
            ThumbLock.Release();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void MakeThumbnail(string source, string target, int size)
    {
        using var img = System.Drawing.Image.FromFile(source);
        // Trim most of the 500 px ocean border so the island fills the little tile.
        var trim = img.Width > 1400 ? OceanMarginPixels - 80 : 0;
        var src = new System.Drawing.Rectangle(trim, trim, img.Width - 2 * trim, img.Height - 2 * trim);
        using var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(img, new System.Drawing.Rectangle(0, 0, size, size), src, System.Drawing.GraphicsUnit.Pixel);
        }
        var tmp = target + ".tmp";
        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Jpeg);
        File.Move(tmp, target, overwrite: true);
    }

    public static MapImage? Resolve(ServerInstance s)
    {
        var f = FindRendered(s);
        if (f is not null)
            return new MapImage($"api/servers/{s.Id}/rendered-map?v={f.LastWriteTimeUtc.Ticks}", "rendered:" + f.Name, true);
        if (!string.IsNullOrWhiteSpace(s.Settings.MapImageUrl))
            return new MapImage(s.Settings.MapImageUrl, s.Settings.MapImageUrl, false);
        return null;
    }

    /// <summary>The hand-tuned ocean border for this image, or null to work it out automatically.</summary>
    public static int? ManualMargin(ServerInstance s, MapImage? image) =>
        image is not null && s.Settings.MapImageMarginSource == image.Key ? s.Settings.MapImageMarginMeters : null;

    /// <summary>Ocean border in metres for an image of the given pixel width (500 px each side at any scale).</summary>
    public static int AutoMargin(int imageWidth, int worldSize)
    {
        var islandPixels = imageWidth - 2 * OceanMarginPixels;
        if (islandPixels <= 0) return 0;
        return (int)Math.Round(OceanMarginPixels * (double)worldSize / islandPixels);
    }
}
