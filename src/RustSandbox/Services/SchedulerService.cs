using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>Runs scheduled restarts, wipes, announcements and commands.</summary>
public sealed class SchedulerService(
    IDbContextFactory<AppDbContext> dbFactory,
    ServerConnectionManager conn,
    ProcessManager processes,
    LiveEvents live,
    ILogger<SchedulerService> log) : BackgroundService
{
    /// <summary>Actions whose in-game countdown should finish at the scheduled time.</summary>
    private static bool HasCountdown(ScheduledTask t) => t.Action is ScheduleAction.Restart or ScheduleAction.MapWipe or ScheduleAction.FullWipe;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Scheduler error"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tasks = await db.Schedules.Where(t => t.Enabled).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var t in tasks)
        {
            if (t.NextRunUtc is null)
            {
                t.NextRunUtc = ComputeNext(t, now);
                changed = true;
                if (t.NextRunUtc is null) { t.Enabled = false; continue; }
            }

            var lead = HasCountdown(t) ? TimeSpan.FromMinutes(Math.Max(0, t.WarningMinutes)) : TimeSpan.Zero;
            if (now < t.NextRunUtc.Value - lead) continue;

            var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == t.ServerId, ct);
            if (server is null) { t.Enabled = false; changed = true; continue; }

            t.LastRunUtc = now;
            t.LastResult = "Running…";
            t.NextRunUtc = t.Kind == ScheduleKind.Once ? null : ComputeNext(t, t.NextRunUtc.Value.AddMinutes(1));
            if (t.Kind == ScheduleKind.Once) t.Enabled = false;
            changed = true;

            var taskId = t.Id;
            log.LogInformation("Running scheduled task {Name} on {Server}", t.Name, server.Name);
            conn.AppendConsole(server.Id, ConsoleKind.System, $"Scheduled task: {t.Name}");
            // Restarts and wipes can take minutes — don't block the scheduler loop.
            _ = Task.Run(async () =>
            {
                string result;
                try { result = await RunTaskAsync(t, server) ?? "OK"; }
                catch (Exception ex) { result = "Failed: " + ex.Message; }
                await SaveResultAsync(taskId, result);
            }, CancellationToken.None);
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            live.RaiseServersChanged();
        }
    }

    private async Task SaveResultAsync(int taskId, string result)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var t = await db.Schedules.FindAsync(taskId);
        if (t is null) return;
        t.LastResult = result.Length > 200 ? result[..200] : result;
        await db.SaveChangesAsync();
    }

    /// <summary>Execute a task now. Returns null on success or an error message.</summary>
    public async Task<string?> RunTaskAsync(ScheduledTask t, ServerInstance server)
    {
        switch (t.Action)
        {
            case ScheduleAction.Restart:
                return await processes.RestartAsync(server, t.WarningMinutes, string.IsNullOrWhiteSpace(t.Payload) ? "Scheduled restart" : t.Payload);
            case ScheduleAction.Save:
                return await conn.QueryAsync(server.Id, "server.save", TimeSpan.FromSeconds(60)) is null ? "Not connected" : null;
            case ScheduleAction.Announce:
                if (string.IsNullOrWhiteSpace(t.Payload)) return "No message set";
                return await conn.QueryAsync(server.Id, $"say \"{t.Payload.Replace("\"", "'")}\"") is null ? "Not connected" : null;
            case ScheduleAction.Command:
                if (string.IsNullOrWhiteSpace(t.Payload)) return "No command set";
                string? last = null;
                foreach (var cmd in t.Payload.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    last = await conn.ExecuteAsync(server.Id, cmd);
                return last is null ? "Not connected" : null;
            case ScheduleAction.UpdateServer:
                return await processes.UpdateAndRestartAsync(server, t.WarningMinutes);
            case ScheduleAction.MapWipe:
                return await processes.WipeAsync(server, blueprints: false, newSeed: t.NewSeedOnWipe, warnMinutes: t.WarningMinutes);
            case ScheduleAction.FullWipe:
                return await processes.WipeAsync(server, blueprints: true, newSeed: t.NewSeedOnWipe, warnMinutes: t.WarningMinutes);
            default:
                return "Unknown action";
        }
    }

    // ---------------------------------------------------------------- next-run calculation

    public static DateTime? ComputeNext(ScheduledTask t, DateTime afterUtc)
    {
        var tz = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, tz);
        var tod = TimeSpan.FromMinutes(Math.Clamp(t.TimeOfDayMinutes, 0, 24 * 60 - 1));

        switch (t.Kind)
        {
            case ScheduleKind.Interval:
                var minutes = Math.Max(1, t.IntervalMinutes);
                var next = (t.LastRunUtc ?? afterUtc).AddMinutes(minutes);
                return next > afterUtc ? next : afterUtc.AddMinutes(minutes);

            case ScheduleKind.Daily:
            {
                var c = nowLocal.Date + tod;
                if (c <= nowLocal) c = c.AddDays(1);
                return ToUtc(c, tz);
            }

            case ScheduleKind.Weekly:
            {
                var days = ((int)t.DayOfWeek - (int)nowLocal.DayOfWeek + 7) % 7;
                var c = nowLocal.Date.AddDays(days) + tod;
                if (c <= nowLocal) c = c.AddDays(7);
                return ToUtc(c, tz);
            }

            case ScheduleKind.FirstThursday:
            {
                for (var m = 0; m < 3; m++)
                {
                    var month = new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(m);
                    var offset = ((int)DayOfWeek.Thursday - (int)month.DayOfWeek + 7) % 7;
                    var c = month.AddDays(offset) + tod;
                    if (c > nowLocal) return ToUtc(c, tz);
                }
                return null;
            }

            case ScheduleKind.Once:
                return t.OnceAtUtc is { } at && at > afterUtc ? at : null;

            default:
                return null;
        }
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1); // skipped by DST
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    public static string Describe(ScheduledTask t)
    {
        var time = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(t.TimeOfDayMinutes)).ToString("h:mm tt");
        return t.Kind switch
        {
            ScheduleKind.Daily => $"Every day at {time}",
            ScheduleKind.Weekly => $"Every {t.DayOfWeek} at {time}",
            ScheduleKind.Interval => t.IntervalMinutes % 60 == 0 ? $"Every {t.IntervalMinutes / 60} hour(s)" : $"Every {t.IntervalMinutes} minutes",
            ScheduleKind.FirstThursday => $"First Thursday of the month at {time} (forced wipe day)",
            ScheduleKind.Once => t.OnceAtUtc is { } at ? $"Once on {TimeZoneInfo.ConvertTimeFromUtc(at, TimeZoneInfo.Local):MMM d, h:mm tt}" : "Once",
            _ => t.Kind.ToString(),
        };
    }
}
