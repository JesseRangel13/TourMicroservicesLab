using Catalog.Api.Application;
using Catalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace Catalog.Api.Images;

public sealed class TourImages(CatalogDb db, IImageStorage storage, TimeProvider clock, ImageUploadLimit limit)
{
    private static string Key(Guid tour, Guid image) => $"tours/{tour:N}/{image:N}";
    public async Task<TourImageView[]> ListAsync(Guid tour, bool admin, bool includePending, CancellationToken ct)
    {
        if (!await db.Tours.AnyAsync(t => t.Id == tour && (admin || t.Active), ct)) throw new CatalogProblem(404, "TourNotFound");
        var rows = await db.Images.AsNoTracking().Where(x => x.TourId == tour && (includePending && admin || x.Status == "Ready"))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(50).ToArrayAsync(ct);
        var result = new List<TourImageView>();
        foreach (var row in rows) {
            CheckKey(row);
            result.Add(View(row, row.Status == "Ready" ? await storage.ReadUrlAsync(row.ObjectKey, ct) : null));
        }
        return result.ToArray();
    }
    public async Task<TourImageView> UploadAsync(Guid tour, Guid id, Stream body, CancellationToken ct,
        Func<CancellationToken, Task>? afterStorage = null)
    {
        if (tour == Guid.Empty || id == Guid.Empty) throw new CatalogProblem(400, "InvalidImageId");
        if (!await limit.Gate.WaitAsync(0, ct)) throw new CatalogProblem(429, "ImageUploadBusy");
        try {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(40));
            await using var image = await ValidatedImage.ReadAsync(body, budget.Token);
            var owner = Guid.NewGuid(); var now = clock.GetUtcNow(); TourImage row;
            await using (var tx = await db.Database.BeginTransactionAsync(budget.Token)) {
                var found = await db.Tours.FromSqlInterpolated($"SELECT * FROM catalog.\"Tours\" WHERE \"Id\"={tour} FOR UPDATE").SingleOrDefaultAsync(budget.Token);
                if (found is null) throw new CatalogProblem(404, "TourNotFound");
                row = await db.Images.SingleOrDefaultAsync(x => x.Id == id, budget.Token) ?? new TourImage {
                    Id = id, TourId = tour, ObjectKey = Key(tour, id), ContentType = image.ContentType,
                    ByteSize = image.Content.Length, Sha256 = image.Sha256, CreatedAtUtc = now };
                if (row.TourId != tour) throw new CatalogProblem(404, "ImageNotFound");
                if (row.Sha256 != image.Sha256) throw new CatalogProblem(409, "ImageIntentionChanged");
                if (row.Status == "Ready") return View(row, null);
                if (row.Status != "Pending") throw new CatalogProblem(409, "ImageDeletionRequested");
                if (row.LeaseUntilUtc > now) throw new CatalogProblem(409, "ImageUploadInProgress");
                if (db.Entry(row).State == EntityState.Detached) {
                    if (await db.Images.CountAsync(x => x.TourId == tour && x.Status != "Deleted", budget.Token) >= 3)
                        throw new CatalogProblem(409, "ImageCountLimit");
                    db.Images.Add(row);
                }
                row.LeaseOwner = owner; row.LeaseUntilUtc = now.AddMinutes(2); row.Version++;
                await db.SaveChangesAsync(budget.Token); await tx.CommitAsync(budget.Token);
            }
            // Intent committed before network I/O. Failure leaves Pending for bounded reconciliation.
            CheckKey(row); await storage.PutAsync(row.ObjectKey, image.Content, image.ContentType, image.Sha256, budget.Token);
            if (afterStorage is not null) await afterStorage(budget.Token);
            await FinishAsync(row.Id, owner, "Pending", "Ready", budget.Token);
            db.ChangeTracker.Clear();
            var current = await db.Images.AsNoTracking().SingleAsync(x => x.Id == id, budget.Token);
            return View(current, null);
        } finally { limit.Gate.Release(); }
    }
    public async Task DeleteAsync(Guid tour, Guid id, CancellationToken ct)
    {
        var count = await db.Images.Where(x => x.Id == id && x.TourId == tour).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, x => x.Status == "Deleted" ? "Deleted" : "DeletePending")
            .SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (count == 0) throw new CatalogProblem(404, "ImageNotFound");
    }
    public async Task<bool> ReconcileAsync(CancellationToken ct, Guid? selected = null)
    {
        var now = clock.GetUtcNow(); var owner = Guid.NewGuid();
        var rows = await db.Images.FromSqlInterpolated($"""
            WITH due AS (SELECT "Id" FROM catalog."TourImages"
              WHERE "Status" IN ('Pending','DeletePending','Deleted') AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc"<={now})
                AND ({selected}::uuid IS NULL OR "Id"={selected})
              ORDER BY "CreatedAtUtc","Id" FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE catalog."TourImages" i SET "LeaseOwner"={owner},"LeaseUntilUtc"={now.AddMinutes(2)},"Version"="Version"+1
              FROM due WHERE i."Id"=due."Id" RETURNING i.*
            """).AsNoTracking().ToListAsync(ct);
        var row = rows.SingleOrDefault(); if (row is null) return false;
        CheckKey(row);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(25));
        if (row.Status is "DeletePending" or "Deleted") {
            await storage.DeleteAsync(row.ObjectKey, budget.Token); await FinishAsync(row.Id, owner, row.Status, "Deleted", ct);
        } else {
            var stored = await storage.HeadAsync(row.ObjectKey, budget.Token);
            if (stored?.Sha256 == row.Sha256 && stored.Size == row.ByteSize) await FinishAsync(row.Id, owner, "Pending", "Ready", ct);
            else if (row.CreatedAtUtc <= now.AddMinutes(-15) || stored is not null) await FinishAsync(row.Id, owner, "Pending", "DeletePending", ct);
            else await FinishAsync(row.Id, owner, "Pending", "Pending", ct);
        }
        return true;
    }
    private Task<int> FinishAsync(Guid id, Guid owner, string from, string to, CancellationToken ct) =>
        db.Images.Where(x => x.Id == id && x.Status == from && x.LeaseOwner == owner && x.LeaseUntilUtc > clock.GetUtcNow())
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, to).SetProperty(x => x.Version, x => x.Version + 1)
                .SetProperty(x => x.LeaseOwner, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, to == "Deleted" ? clock.GetUtcNow().AddHours(1) : clock.GetUtcNow().AddSeconds(15)), ct);
    private static void CheckKey(TourImage row) { if (row.ObjectKey != Key(row.TourId, row.Id)) throw new CatalogProblem(409, "InvalidOwnedImageKey"); }
    private static TourImageView View(TourImage row, string? url) => new(row.Id, row.TourId, row.ContentType, row.ByteSize, row.Status, row.Version, url);
}

public sealed class ImageRecoveryWorker(IServiceScopeFactory scopes, IOptions<ImageStorageOptions> options, ILogger<ImageRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled) return;
        while (!stop.IsCancellationRequested) {
            try {
                for (var i = 0; i < 10; i++) {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<TourImages>().ReconcileAsync(stop)) break;
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Image reconciliation failed; durable work remains retryable"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stop);
        }
    }
}
