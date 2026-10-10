using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RustSandbox.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ServerInstance> Servers => Set<ServerInstance>();
    public DbSet<KillEvent> Kills => Set<KillEvent>();
    public DbSet<PositionSample> Positions => Set<PositionSample>();
    public DbSet<ConnectionEvent> Connections => Set<ConnectionEvent>();
    public DbSet<WeaponUsage> WeaponUsage => Set<WeaponUsage>();
    public DbSet<GatherStat> Gathering => Set<GatherStat>();
    public DbSet<SavedSeed> Seeds => Set<SavedSeed>();
    public DbSet<ScheduledTask> Schedules => Set<ScheduledTask>();
    public DbSet<CommandMacro> Macros => Set<CommandMacro>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Go through the property (not the backing field) so edits to Settings are serialized on save.
        b.Entity<ServerInstance>().Property(s => s.SettingsJson).HasColumnName("Settings")
            .UsePropertyAccessMode(PropertyAccessMode.Property);
        b.Entity<ServerInstance>().Ignore(s => s.Settings);

        b.Entity<KillEvent>().HasIndex(k => new { k.ServerId, k.Timestamp });
        b.Entity<KillEvent>().HasIndex(k => new { k.ServerId, k.KillerId });
        b.Entity<KillEvent>().HasIndex(k => new { k.ServerId, k.VictimId });
        b.Entity<PositionSample>().HasIndex(p => new { p.ServerId, p.Timestamp });
        b.Entity<ConnectionEvent>().HasIndex(c => new { c.ServerId, c.Timestamp });
        b.Entity<WeaponUsage>().HasIndex(w => new { w.ServerId, w.SteamId, w.Weapon, w.Day }).IsUnique();
        b.Entity<GatherStat>().HasIndex(g => new { g.ServerId, g.SteamId, g.Item, g.Source, g.Day }).IsUnique();
        b.Entity<GatherStat>().HasIndex(g => new { g.ServerId, g.Day });
        b.Entity<ScheduledTask>().HasIndex(t => t.ServerId);

        // SQLite gives DateTimes back as Unspecified; everything here is stored as UTC.
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
            }
        }
    }
}
