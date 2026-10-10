using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>
/// Rust uploads a picture of its map to Facepunch when the server starts and logs the URL
/// ("[Rust.MapCache-Images] Image uploaded to backend: https://files.facepunch.com/rust/map-images/…jpg").
/// This watches the console for that line and uses the image as the live-map background.
/// A map image the person entered themselves is never replaced.
/// </summary>
public sealed partial class MapImageWatcher(
    LiveEvents live,
    ServerConnectionManager conn,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<MapImageWatcher> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        live.ConsoleLineAdded += OnLine;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        live.ConsoleLineAdded -= OnLine;
        return Task.CompletedTask;
    }

    private void OnLine(int serverId, ConsoleLine line)
    {
        if (line.Kind is ConsoleKind.Command or ConsoleKind.System) return;
        if (!line.Text.Contains("files.facepunch.com/rust/map-images", StringComparison.OrdinalIgnoreCase)) return;
        var m = MapImageRegex().Match(line.Text);
        if (m.Success) _ = SaveAsync(serverId, m.Value);
    }

    private async Task SaveAsync(int serverId, string url)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == serverId);
            if (server is null || server.IsDemo) return;
            var settings = server.Settings;
            var userImage = !settings.MapImageAuto && !string.IsNullOrWhiteSpace(settings.MapImageUrl);
            if (userImage || settings.MapImageUrl == url) return;

            settings.MapImageUrl = url;
            settings.MapImageAuto = true;
            await db.SaveChangesAsync();
            conn.UpdateServer(server);
            conn.AppendConsole(serverId, ConsoleKind.System, "Live map now shows the server's real map image.");
            live.RaiseServersChanged();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't save the map image for server {Id}", serverId);
        }
    }

    [GeneratedRegex(@"https://files\.facepunch\.com/rust/map-images/\S+?\.(?:jpg|jpeg|png)", RegexOptions.IgnoreCase)]
    private static partial Regex MapImageRegex();
}
