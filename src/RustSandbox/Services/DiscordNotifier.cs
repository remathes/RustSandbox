using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>Posts server events to a per-server Discord webhook.</summary>
public sealed class DiscordNotifier(IHttpClientFactory http, IDbContextFactory<AppDbContext> dbFactory, ILogger<DiscordNotifier> log)
{
    public const int Green = 0x3FB37F, Red = 0xE5484D, Orange = 0xCE422B, Amber = 0xF0A030, Blue = 0x4C8DF6;

    private async Task<ServerInstance?> LoadAsync(int serverId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId);
    }

    public async Task NotifyAsync(int serverId, string title, string description, int color, bool isStatus = false)
    {
        try
        {
            var server = await LoadAsync(serverId);
            if (server is null || string.IsNullOrWhiteSpace(server.Settings.DiscordWebhookUrl)) return;
            if (isStatus && !server.Settings.NotifyStatusChanges) return;
            await SendAsync(server.Settings.DiscordWebhookUrl, server.Name, title, description, color);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Discord notification failed for server {Id}", serverId);
        }
    }

    public async Task NotifyLongKillAsync(int serverId, KillEvent k)
    {
        try
        {
            var server = await LoadAsync(serverId);
            if (server is null || !server.Settings.NotifyLongKills || string.IsNullOrWhiteSpace(server.Settings.DiscordWebhookUrl)) return;
            if (k.Distance < server.Settings.LongKillMeters) return;
            await SendAsync(server.Settings.DiscordWebhookUrl, server.Name, "🎯 Long-range kill",
                $"**{k.KillerName}** killed **{k.VictimName}** from **{k.Distance:0} m** with {k.Weapon}{(k.IsHeadshot ? " (headshot)" : "")}", Orange);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Discord kill notification failed");
        }
    }

    /// <summary>Returns null on success, otherwise an error message (used by the Settings "Test" button).</summary>
    public async Task<string?> TestAsync(string webhookUrl, string serverName)
    {
        try
        {
            await SendAsync(webhookUrl, serverName, "✅ Webhook connected", "Rust Sandbox will post server events here.", Green);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task SendAsync(string url, string serverName, string title, string description, int color)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Webhook URL must be an https:// URL.");
        var client = http.CreateClient("discord");
        var payload = new
        {
            username = "Rust Sandbox",
            embeds = new[]
            {
                new { title, description, color, footer = new { text = serverName }, timestamp = DateTime.UtcNow.ToString("o") },
            },
        };
        using var resp = await client.PostAsJsonAsync(uri, payload);
        resp.EnsureSuccessStatusCode();
    }
}
