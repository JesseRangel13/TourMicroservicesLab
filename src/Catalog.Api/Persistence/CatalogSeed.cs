using Microsoft.EntityFrameworkCore;
namespace Catalog.Api.Persistence;
public static class CatalogSeed
{
    public static async Task RunAsync(CatalogDb db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Application.CatalogRules.LockAsync(db, "catalog/sample-seed", ct);
        foreach (var (id, name, amount) in new[] {
            (Guid.Parse("10000000-0000-0000-0000-000000000001"), "Historic city walk", 25000L),
            (Guid.Parse("10000000-0000-0000-0000-000000000002"), "Mountain day trip", 80000L) })
        {
            if (!await db.Tours.AnyAsync(t => t.Id == id, ct)) db.Tours.Add(new Tour { Id = id, Name = name, Description = "Sample lab tour. Reservations become available in LAB-003." });
            var sessionId = new Guid(id.ToString().Replace("10000000", "20000000", StringComparison.Ordinal));
            if (!await db.Sessions.AnyAsync(s => s.Id == sessionId, ct)) db.Sessions.Add(new TourSession { Id = sessionId,
                TourId = id, StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30), UnitAmountMinor = amount, Capacity = 12, AvailableSeats = 12 });
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
