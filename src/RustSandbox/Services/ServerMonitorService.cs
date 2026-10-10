using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;
using RustSandbox.Services.Rcon;

namespace RustSandbox.Services;

/// <summary>
/// Keeps an RCON connection open to every server, polls serverinfo/playerlist for the dashboard,
/// raises up/down notifications and prunes old history.
/// </summary>
public sealed class ServerMonitorService(
    IServiceProvider services,
    ServerConnectionManager conn,
    ProcessManager processes,
    DiscordNotifier discord,
    LiveEvents live,
    LiveWorldStore world,
    IConfiguration config,
    ILogger<ServerMonitorService> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private List<ServerInstance> _servers = new();
    private DateTime _serversLoaded = DateTime.MinValue;
    private DateTime _lastCleanup = DateTime.MinValue;
    private volatile bool _reload = true;
    private long _tick;
    private readonly DateTime _managerStarted = DateTime.UtcNow;
    private readonly ConcurrentDictionary<int, DateTime> _lastPluginReload = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        live.ServersChanged += () => _reload = true;

        try
        {
            await processes.AutoStartAsync(await LoadServersAsync(stoppingToken));
        }
        catch (Exception ex) { log.LogWarning(ex, "Auto-start failed"); }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                if (_reload || DateTime.UtcNow - _serversLoaded > TimeSpan.FromSeconds(30))
                {
                    _servers = await LoadServersAsync(stoppingToken);
                    foreach (var s in _servers) conn.UpdateServer(s);
                }
                _tick++;
                var includePlayers = _tick % 2 == 0;
                await Task.WhenAll(_servers.Select(s => PollAsync(s, includePlayers, stoppingToken)));

                if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
                {
                    _lastCleanup = DateTime.UtcNow;
                    await CleanupAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Monitor loop error");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<List<ServerInstance>> LoadServersAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Servers.AsNoTracking().ToListAsync(ct);
        _serversLoaded = DateTime.UtcNow;
        _reload = false;
        return list;
    }

    private async Task PollAsync(ServerInstance s, bool includePlayers, CancellationToken ct)
    {
        var rt = conn.Get(s.Id);
        try
        {
            // Don't hammer a managed server that isn't running.
            var proc = processes.Get(s.Id);
            if (s.Settings.ManageProcess && !s.IsDemo && !proc.IsRunning && !processes.TryAdopt(s))
            {
                if (rt.IsOnline) await conn.DisconnectAsync(s.Id);
                else if (rt.State != ConnectionState.Offline || rt.LastError != null)
                {
                    rt.State = ConnectionState.Offline;
                    rt.LastError = null;
                    live.RaiseStatus(s.Id);
                }
                await TrackUpDownAsync(s, rt, online: false);
                return;
            }

            if (!rt.IsOnline && !await conn.EnsureConnectedAsync(s, ct: ct))
            {
                await TrackUpDownAsync(s, rt, online: false);
                return;
            }

            var infoJson = await conn.QueryAsync(s.Id, "serverinfo", TimeSpan.FromSeconds(5), ct);
            var info = infoJson is null ? null : RconParsers.ParseServerInfo(infoJson);
            if (info is not null)
            {
                rt.Info = info;
                rt.InfoUpdatedUtc = DateTime.UtcNow;
                rt.Metrics.Add(new MetricPoint(DateTime.UtcNow, info.Framerate, info.Players, info.EntityCount, info.Memory, info.NetworkIn, info.NetworkOut, info.Queued));
            }

            if (includePlayers || rt.Players.Count == 0)
            {
                var plJson = await conn.QueryAsync(s.Id, "playerlist", TimeSpan.FromSeconds(5), ct);
                var players = plJson is null ? null : RconParsers.ParsePlayerList(plJson);
                if (players is not null) rt.Players = players;
            }

            await TrackUpDownAsync(s, rt, online: rt.IsOnline);
            await TelemetryWatchdogAsync(s, rt.Info, ct);
            live.RaiseStatus(s.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Poll failed for server {Id}", s.Id);
        }
    }

    /// <summary>
    /// If the telemetry plugin is loaded but nothing has arrived for 2 minutes, reload it
    /// (at most every 10 minutes per server). Covers a plugin that got stuck silently.
    /// </summary>
    private async Task TelemetryWatchdogAsync(ServerInstance s, ServerInfo? info, CancellationToken ct)
    {
        if (s.IsDemo || s.Settings.Framework == ModFramework.Vanilla || info is null) return;
        if (info.Uptime < 180 || DateTime.UtcNow - _managerStarted < TimeSpan.FromMinutes(2)) return;
        if (world.LastTelemetryUtc(s.Id) is { } last && DateTime.UtcNow - last < TimeSpan.FromMinutes(2)) return;
        if (_lastPluginReload.TryGetValue(s.Id, out var tried) && DateTime.UtcNow - tried < TimeSpan.FromMinutes(10)) return;
        _lastPluginReload[s.Id] = DateTime.UtcNow;

        var prefix = PluginService.Prefix(s.Settings);
        var list = await conn.QueryAsync(s.Id, prefix + "plugins", TimeSpan.FromSeconds(8), ct);
        if (list is null || !list.Contains("RustSandboxTelemetry", StringComparison.OrdinalIgnoreCase)) return; // not installed
        conn.AppendConsole(s.Id, ConsoleKind.Warning, "Telemetry plugin is loaded but no reports for 2+ minutes — reloading it.");
        await conn.QueryAsync(s.Id, prefix + "reload RustSandboxTelemetry", TimeSpan.FromSeconds(10), ct);
    }

    private Task TrackUpDownAsync(ServerInstance s, ServerRuntime rt, bool online)
    {
        if (s.IsDemo) return Task.CompletedTask;
        var was = rt.WasOnline;
        rt.WasOnline = online;
        if (was is null || was == online) return Task.CompletedTask;
        return online
            ? discord.NotifyAsync(s.Id, "🟢 Server online", $"{s.Settings.Hostname} is up and accepting connections.", DiscordNotifier.Green, isStatus: true)
            : discord.NotifyAsync(s.Id, "🔴 Server offline", $"Lost contact with {s.Settings.Hostname}.", DiscordNotifier.Red, isStatus: true);
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var days = Math.Max(1, config.GetValue("Manager:RetentionDays", 30));
        var cutoff = DateTime.UtcNow.AddDays(-days);
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var positions = await db.Positions.Where(p => p.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        var conns = await db.Connections.Where(c => c.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        // Kills are small and make up the long-term stats, so keep them 4x longer.
        var kills = await db.Kills.Where(k => k.Timestamp < cutoff.AddDays(-days * 3)).ExecuteDeleteAsync(ct);
        if (positions + conns + kills > 0)
            log.LogInformation("Retention cleanup removed {P} positions, {C} connection events, {K} kills", positions, conns, kills);
    }
}
