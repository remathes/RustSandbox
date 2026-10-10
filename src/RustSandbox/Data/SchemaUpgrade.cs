using Microsoft.EntityFrameworkCore;

namespace RustSandbox.Data;

/// <summary>
/// EnsureCreated only builds a brand-new database, so tables added in later versions are created here
/// for databases that already exist. Uses EF's own create script so the schema matches the model.
/// </summary>
public static class SchemaUpgrade
{
    public static async Task ApplyAsync(AppDbContext db, ILogger log)
    {
        var existing = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var statements = db.Database.GenerateCreateScript()
            .Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

        foreach (var table in statements.Select(TableOf).Where(t => t is not null && !have.Contains(t!)).Distinct().ToList())
        {
            foreach (var sql in statements.Where(s => TableOf(s) == table))
                await db.Database.ExecuteSqlRawAsync(sql);
            log.LogInformation("Database upgraded: added table {Table}", table);
        }

        // The project was renamed from Rust Server Manager: scheduled commands use the new plugin command names.
        if (have.Contains("Schedules"))
        {
            var renamed = await db.Database.ExecuteSqlRawAsync(
                "UPDATE Schedules SET Payload = replace(replace(Payload, 'rsmskills.', 'sandboxskills.'), 'rsmtweaks.', 'sandboxtweaks.') " +
                "WHERE Payload LIKE '%rsmskills.%' OR Payload LIKE '%rsmtweaks.%'");
            if (renamed > 0) log.LogInformation("Updated {Count} scheduled command(s) to the new plugin command names", renamed);
        }
    }

    /// <summary>The table a CREATE TABLE / CREATE INDEX statement belongs to.</summary>
    private static string? TableOf(string sql)
    {
        var m = System.Text.RegularExpressions.Regex.Match(sql,
            @"^CREATE\s+(?:TABLE\s+""(?<t>[^""]+)""|(?:UNIQUE\s+)?INDEX\s+""[^""]+""\s+ON\s+""(?<t>[^""]+)"")",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["t"].Value : null;
    }
}
