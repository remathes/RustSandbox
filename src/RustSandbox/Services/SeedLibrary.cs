using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

/// <summary>Random seed generation and the saved-seed library.</summary>
public sealed class SeedLibrary(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MinSize = 1000;
    public const int MaxSize = 6000;

    /// <summary>A cryptographically random seed in Rust's valid range (1 – 2,147,483,646).</summary>
    public static int RandomSeed() => RandomNumberGenerator.GetInt32(1, int.MaxValue);

    /// <summary>Rough size guidance shown in the generator.</summary>
    public static string SizeAdvice(int size, int maxPlayers = 0) => size switch
    {
        < 2000 => "Tiny — 1v1 / build testing",
        < 3000 => "Small — up to ~50 players, constant fighting",
        < 3750 => "Medium — 50–100 players",
        < 4500 => "Standard — 100–200 players (Facepunch official ~4250)",
        < 5000 => "Large — 200+ players",
        _ => "Huge — high pop, long travel, heavier on RAM",
    };

    public static string RustMapsUrl(int seed, int size) => $"https://rustmaps.com/map/{size}_{seed}";

    public async Task<List<SavedSeed>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Seeds.AsNoTracking().OrderByDescending(s => s.IsFavorite).ThenByDescending(s => s.CreatedUtc).ToListAsync();
    }

    public async Task<SavedSeed> SaveAsync(SavedSeed seed)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (seed.Id == 0)
        {
            var existing = await db.Seeds.FirstOrDefaultAsync(s => s.Seed == seed.Seed && s.WorldSize == seed.WorldSize);
            if (existing is not null) return existing;
            db.Seeds.Add(seed);
        }
        else db.Seeds.Update(seed);
        await db.SaveChangesAsync();
        return seed;
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Seeds.Where(s => s.Id == id).ExecuteDeleteAsync();
    }
}
