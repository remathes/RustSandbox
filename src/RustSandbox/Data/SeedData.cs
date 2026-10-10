using Microsoft.EntityFrameworkCore;

namespace RustSandbox.Data;

/// <summary>First-run content: a demo server to explore and a starter set of console macros.</summary>
public static class SeedData
{
    public static async Task EnsureAsync(AppDbContext db, IConfiguration config)
    {
        if (!await db.Servers.AnyAsync() && config.GetValue("Manager:CreateDemoServer", true))
        {
            var demo = new ServerInstance
            {
                Name = "Demo Server",
                Color = "#ce422b",
                IsDemo = true,
                SortOrder = 0,
                LastWipeUtc = DateTime.UtcNow.AddDays(-3),
                Settings = new ServerSettings
                {
                    Hostname = "Demo Server | Monthly | Vanilla+",
                    Identity = "demo",
                    Description = "A simulated server so you can explore the manager. Add your real server from the sidebar.",
                    Seed = 1337,
                    WorldSize = 4000,
                    RconPassword = "demo",
                    Tags = "monthly,vanilla,NA",
                },
            };
            db.Servers.Add(demo);
            await db.SaveChangesAsync();

            db.Schedules.AddRange(
                new ScheduledTask { ServerId = demo.Id, Name = "Nightly restart", Kind = ScheduleKind.Daily, Action = ScheduleAction.Restart, TimeOfDayMinutes = 5 * 60, WarningMinutes = 10, Enabled = false },
                new ScheduledTask { ServerId = demo.Id, Name = "Discord reminder", Kind = ScheduleKind.Interval, Action = ScheduleAction.Announce, IntervalMinutes = 30, Payload = "Join our Discord: discord.gg/example", Enabled = false },
                new ScheduledTask { ServerId = demo.Id, Name = "Forced wipe", Kind = ScheduleKind.FirstThursday, Action = ScheduleAction.MapWipe, TimeOfDayMinutes = 13 * 60, WarningMinutes = 15, NewSeedOnWipe = true, Enabled = false });
            await db.SaveChangesAsync();
        }

        if (!await db.Macros.AnyAsync())
        {
            var i = 0;
            db.Macros.AddRange(
                new CommandMacro { Label = "Save world", Command = "server.save", SortOrder = i++ },
                new CommandMacro { Label = "Status", Command = "status", SortOrder = i++ },
                new CommandMacro { Label = "Make it day", Command = "env.time 12", SortOrder = i++ },
                new CommandMacro { Label = "Make it night", Command = "env.time 0", SortOrder = i++ },
                new CommandMacro { Label = "Clear weather", Command = "weather.load Clear", SortOrder = i++ },
                new CommandMacro { Label = "Write config", Command = "server.writecfg", SortOrder = i++ },
                new CommandMacro { Label = "List plugins", Command = "oxide.plugins", SortOrder = i++ },
                new CommandMacro { Label = "Reload all plugins", Command = "oxide.reload *", Confirm = true, SortOrder = i++ });
            await db.SaveChangesAsync();
        }
    }
}
