using System.Diagnostics;
using System.Text.Json;
using Catalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
namespace Catalog.Api.Application;

public sealed class CatalogProblem(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class CatalogRules
{
    public static void Require(bool valid, string code = "InvalidInput", int status = 400)
    { if (!valid) throw new CatalogProblem(status, code); }
    public static void Text(string? name, string? description) => Require(!string.IsNullOrWhiteSpace(name)
        && name.Length <= 200 && description is not null && description.Length <= 4000);
    public static void Version(long actual, long expected)
    { Require(expected > 0); Require(actual == expected, "VersionConflict", 409); }
    public static TourView View(Tour t) => new(t.Id, t.Name, t.Description, t.Active, t.Version);
    public static SessionView View(TourSession s) => new(s.Id, s.TourId, s.StartsAtUtc, s.UnitAmountMinor,
        s.Currency, s.Capacity, s.AvailableSeats, s.PriceVersion, s.Version);
    // The lock is held by PostgreSQL until commit/rollback, including across separate API replicas.
    public static Task<int> LockAsync(CatalogDb db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    public static async Task<Tour> TourAsync(CatalogDb db, Guid id, CancellationToken ct)
    {
        var rows = await db.Tours.FromSqlInterpolated($"SELECT * FROM catalog.\"Tours\" WHERE \"Id\"={id} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new CatalogProblem(404, "TourNotFound");
    }
    public static async Task<TourSession> SessionAsync(CatalogDb db, Guid id, CancellationToken ct)
    {
        var rows = await db.Sessions.FromSqlInterpolated($"SELECT * FROM catalog.\"Sessions\" WHERE \"Id\"={id} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new CatalogProblem(404, "SessionNotFound");
    }
    public static async Task AddIntentAsync<T>(CatalogDb db, string effectKey, T payload, DateTimeOffset now,
        Guid? sagaId, Guid correlationId, Guid causationId, CancellationToken ct)
    {
        var existing=db.Outbox.Local.SingleOrDefault(x=>x.EffectKey==effectKey)??await db.Outbox.SingleOrDefaultAsync(x=>x.EffectKey==effectKey,ct);
        if(existing is not null)
        {
            // A new release command may repair a lost/DLQ response without restoring inventory again.
            if(payload is SeatsReleased && existing.PublishedAtUtc is not null)
            {existing.PublishedAtUtc=null;existing.LeaseOwner=null;existing.LeaseUntilUtc=null;}
            return;
        }
        var messageId = Guid.NewGuid(); var deliveryId = Guid.NewGuid();
        var envelope = new IntegrationEnvelope<T>(messageId, deliveryId, typeof(T).Name, 1, now,
            sagaId, correlationId, causationId, Activity.Current?.Id, payload) { Tracestate = Activity.Current?.TraceStateString };
        db.Outbox.Add(new CatalogOutbox { DeliveryId = deliveryId, MessageId = messageId, EffectKey = effectKey,
            Type = typeof(T).Name, OccurredAtUtc = now,
            EnvelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
    }
    public static Task ProjectionAsync(CatalogDb db, Tour tour, TourSession session, DateTimeOffset now, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        return AddIntentAsync(db, $"session/{session.Id}/{session.PriceVersion}", new TourSessionChanged(session.Id,
            tour.Name, tour.Active, session.PriceVersion, session.UnitAmountMinor, session.Currency), now, null, requestId, requestId, ct);
    }
}
public sealed class CatalogQueries(CatalogDb db)
{
    public async Task<TourPage> SearchAsync(string? search, int page, int pageSize, bool admin, CancellationToken ct)
    {
        CatalogRules.Require(page >= 1 && pageSize is >= 1 and <= 50 && (long)(page - 1) * pageSize <= int.MaxValue
            && (search is null || search.Length <= 200));
        var query = db.Tours.AsNoTracking().Where(t => admin || t.Active);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(t => t.Name.ToLower().Contains(search.ToLower()));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(t => t.Name).ThenBy(t => t.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(CatalogRules.View).ToArray(), total, page, pageSize);
    }
    public async Task<TourView> DetailAsync(Guid id, bool admin, CancellationToken ct) => CatalogRules.View(
        await db.Tours.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && (admin || t.Active), ct)
        ?? throw new CatalogProblem(404, "TourNotFound"));
    public async Task<SessionView[]> SessionsAsync(Guid id, bool admin, CancellationToken ct)
    {
        await DetailAsync(id, admin, ct);
        var rows = await db.Sessions.AsNoTracking().Where(s => s.TourId == id)
            .OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Id).ToArrayAsync(ct);
        return rows.Select(CatalogRules.View).ToArray();
    }
}
public sealed class CatalogCommands(CatalogDb db, TimeProvider clock)
{
    public async Task<TourView> CreateAsync(CreateTour command, CancellationToken ct)
    {
        CatalogRules.Text(command.Name, command.Description);
        var tour = new Tour { Id = Guid.NewGuid(), Name = command.Name!.Trim(), Description = command.Description! };
        db.Tours.Add(tour); await db.SaveChangesAsync(ct); return CatalogRules.View(tour);
    }
    public async Task<TourView> UpdateAsync(Guid id, UpdateTour command, CancellationToken ct)
    {
        CatalogRules.Text(command.Name, command.Description);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tour = await CatalogRules.TourAsync(db, id, ct); CatalogRules.Version(tour.Version, command.ExpectedVersion);
        var changed = tour.Name != command.Name!.Trim() || tour.Active != command.Active;
        tour.Name = command.Name.Trim(); tour.Description = command.Description!; tour.Active = command.Active; tour.Version = checked(tour.Version + 1);
        if (changed)
        {
            var ids = await db.Sessions.Where(s => s.TourId == id).OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync(ct);
            foreach (var sessionId in ids)
            {
                var session = await CatalogRules.SessionAsync(db, sessionId, ct);
                session.PriceVersion = checked(session.PriceVersion + 1); session.Version = checked(session.Version + 1);
                await CatalogRules.ProjectionAsync(db, tour, session, clock.GetUtcNow(), ct);
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CatalogRules.View(tour);
    }
    public async Task<SessionView> CreateSessionAsync(Guid tourId, CreateSession command, CancellationToken ct)
    {
        CatalogRules.Require(command.StartsAtUtc.Offset == TimeSpan.Zero && command.StartsAtUtc > clock.GetUtcNow()
            && command.UnitAmountMinor > 0 && command.Capacity >= 0);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tour = await CatalogRules.TourAsync(db, tourId, ct);
        var session = new TourSession { Id = Guid.NewGuid(), TourId = tourId, StartsAtUtc = command.StartsAtUtc,
            UnitAmountMinor = command.UnitAmountMinor, Capacity = command.Capacity, AvailableSeats = command.Capacity };
        db.Sessions.Add(session); await CatalogRules.ProjectionAsync(db, tour, session, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CatalogRules.View(session);
    }
    public async Task<SessionView> PriceAsync(Guid id, UpdatePrice command, CancellationToken ct)
    {
        CatalogRules.Require(command.UnitAmountMinor > 0);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tourId = await db.Sessions.Where(s => s.Id == id).Select(s => (Guid?)s.TourId).SingleOrDefaultAsync(ct)
            ?? throw new CatalogProblem(404, "SessionNotFound");
        var tour = await CatalogRules.TourAsync(db, tourId, ct); var session = await CatalogRules.SessionAsync(db, id, ct);
        CatalogRules.Version(session.Version, command.ExpectedVersion);
        session.UnitAmountMinor = command.UnitAmountMinor; session.PriceVersion = checked(session.PriceVersion + 1); session.Version = checked(session.Version + 1);
        await CatalogRules.ProjectionAsync(db, tour, session, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CatalogRules.View(session);
    }
    public async Task<SessionView> CapacityAsync(Guid id, UpdateCapacity command, CancellationToken ct)
    {
        CatalogRules.Require(command.Capacity >= 0);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await CatalogRules.SessionAsync(db, id, ct); CatalogRules.Version(session.Version, command.ExpectedVersion);
        var used = session.Capacity - session.AvailableSeats;
        CatalogRules.Require(command.Capacity >= used, "CapacityOccupied", 409);
        session.Capacity = command.Capacity; session.AvailableSeats = command.Capacity - used; session.Version = checked(session.Version + 1);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CatalogRules.View(session);
    }
}
public sealed class QuoteHandler(CatalogDb db, TimeProvider clock)
{
    public async Task<QuoteView> HandleAsync(CreateQuote command, CancellationToken ct)
    {
        CatalogRules.Require(command.RequestId != Guid.Empty && command.SessionId != Guid.Empty && command.Participants is >= 1 and <= 6
            && !string.IsNullOrWhiteSpace(command.UserId) && command.UserId.Length <= 128 && command.ExpectedUnitAmountMinor > 0
            && !string.IsNullOrWhiteSpace(command.ExpectedCurrency));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CatalogRules.LockAsync(db, $"quote/{command.RequestId}", ct);
        var quote = await db.Quotes.SingleOrDefaultAsync(q => q.RequestId == command.RequestId, ct);
        if (quote is not null)
        {
            CatalogRules.Require(quote.SessionId == command.SessionId && quote.Participants == command.Participants && quote.UserId == command.UserId
                && quote.UnitAmountMinor == command.ExpectedUnitAmountMinor && quote.Currency == command.ExpectedCurrency, "RequestIdConflict", 409);
            CatalogRules.Require(quote.ExpiresAtUtc > clock.GetUtcNow(), "QuoteExpired", 409);
        }
        var tourId = await db.Sessions.Where(s => s.Id == command.SessionId).Select(s => (Guid?)s.TourId).SingleOrDefaultAsync(ct)
            ?? throw new CatalogProblem(404, "SessionNotFound");
        var tour = await CatalogRules.TourAsync(db, tourId, ct); var session = await CatalogRules.SessionAsync(db, command.SessionId, ct);
        CatalogRules.Require(tour.Active && session.StartsAtUtc > clock.GetUtcNow(), "SessionUnavailable", 409);
        if (quote is null)
        {
            CatalogRules.Require(session.UnitAmountMinor == command.ExpectedUnitAmountMinor && session.Currency == command.ExpectedCurrency, "PriceChanged", 409);
            long total; try { total = checked(command.Participants * session.UnitAmountMinor); }
            catch (OverflowException) { throw new CatalogProblem(400, "AmountOverflow"); }
            quote = new Quote { Id = Guid.NewGuid(), RequestId = command.RequestId, SessionId = session.Id,
                UserId = command.UserId!, Participants = command.Participants, UnitAmountMinor = session.UnitAmountMinor,
                TotalAmountMinor = total, Currency = session.Currency, ExpiresAtUtc = clock.GetUtcNow().AddMinutes(5) };
            db.Quotes.Add(quote); await db.SaveChangesAsync(ct);
        }
        CatalogRules.Require(quote.ExpiresAtUtc > clock.GetUtcNow(), "QuoteExpired", 409);
        await tx.CommitAsync(ct);
        return new(quote.Id, quote.UnitAmountMinor, quote.TotalAmountMinor, quote.Currency, quote.ExpiresAtUtc, CatalogRules.View(session));
    }
}
