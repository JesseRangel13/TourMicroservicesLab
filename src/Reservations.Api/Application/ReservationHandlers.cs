using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Reservations.Api.Application;

public sealed class ReservationProblem(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class ReservationIntents
{
    public static void Add<T>(ReservationsDb db, T payload, string key, DateTimeOffset now, Guid sagaId, Guid correlationId, Guid causationId)
    {
        var messageId = Guid.NewGuid();
        foreach (var destination in MessageRoutes.Destinations(typeof(T).Name))
        {
            var deliveryId = Guid.NewGuid();
            db.Outbox.Add(new ReservationOutbox { DeliveryId = deliveryId, MessageId = messageId, Destination = destination,
                Type = typeof(T).Name, EffectKey = key, OccurredAtUtc = now,
                EnvelopeJson = MessageRoutes.Serialize(payload, messageId, deliveryId, now, sagaId, correlationId, causationId) });
        }
    }
}
public sealed class ReservationCreator(ReservationsDb db, ICatalogQuoteClient catalog, TimeProvider clock,SagaRecoveryOptions? options=null)
{
    public static Guid QuoteRequestId(string user, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { "tourlab/reservation-create/v1", user, key })));
        return new Guid(bytes.AsSpan(0, 16));
    }
    public async Task<IdempotencyRequest> CreateAsync(string userId, string? key, CreateReservation command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128 || string.IsNullOrWhiteSpace(key) || key.Length > 128
            || key.Any(char.IsControl) || command.SessionId == Guid.Empty || command.Participants is < 1 or > 6
            || command.ExpectedUnitAmountMinor <= 0 || command.ExpectedCurrency != "MXN") throw new ReservationProblem(400, "InvalidInput");
        try { _ = checked(command.ExpectedUnitAmountMinor * command.Participants); }
        catch (OverflowException) { throw new ReservationProblem(400, "AmountOverflow"); }
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, MessageCodec.Json)));
        var existing = await ExistingAsync(userId, key, hash, ct); if (existing is not null) return existing;
        var requestId = QuoteRequestId(userId, key);
        // No database transaction is held while a bounded, authenticated HTTP request executes.
        var quote = await catalog.CreateAsync(new(requestId, command.SessionId, command.Participants, userId,
            command.ExpectedUnitAmountMinor, command.ExpectedCurrency), ct);
        if (quote.ExpiresAtUtc <= clock.GetUtcNow()) throw new ReservationProblem(409, "QuoteExpired");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var reservation = new Reservation { Id = Guid.NewGuid(), UserId = userId, SessionId = command.SessionId,
            Participants = command.Participants, QuoteId = quote.QuoteId, UnitAmountMinor = quote.UnitAmountMinor,
            TotalAmountMinor = quote.TotalAmountMinor, Currency = quote.Currency, StartsAtUtc = quote.Session.StartsAtUtc, CreatedAtUtc = now };
        var saga = new ReservationSaga { Id = Guid.NewGuid(), ReservationId = reservation.Id, HoldId = Guid.NewGuid(),
            PaymentOperationId = Guid.NewGuid(), RefundOperationId = Guid.NewGuid(), DeadlineUtc = now.AddSeconds(options?.AvailabilitySeconds??30) };
        var result = new IdempotencyRequest { UserId = userId, Key = key, RequestHash = hash, ResourceId = reservation.Id,
            Location = $"/api/reservations/v1/reservations/{reservation.Id}",
            ResponseBody = JsonSerializer.Serialize(new ReservationAccepted(reservation.Id, "AwaitingAvailability", saga.Id), MessageCodec.Json) };
        db.Reservations.Add(reservation); db.Sagas.Add(saga); db.Idempotency.Add(result);
        db.History.Add(new TransitionHistory { Id = Guid.NewGuid(), SagaId = saga.Id, MessageId = requestId,
            From = "None", To = saga.State, OccurredAtUtc = now });
        ReservationIntents.Add(db, new HoldSeats(saga.HoldId, quote.QuoteId, reservation.Id, userId), $"saga/{saga.Id}/hold", now, saga.Id, saga.Id, requestId);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result; }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return await ExistingAsync(userId, key, hash, ct) ?? throw new ReservationProblem(409, "CreationInProgress");
        }
    }
    private async Task<IdempotencyRequest?> ExistingAsync(string userId, string key, string hash, CancellationToken ct)
    {
        var row = await db.Idempotency.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.OperationName == "reservation-create" && r.Key == key, ct);
        if (row is not null && row.RequestHash != hash) throw new ReservationProblem(409, "IdempotencyConflict");
        return row;
    }
}
public sealed class ReservationQueries(ReservationsDb db)
{
    public static ReservationView View(Reservation r) => new(r.Id, r.UserId, r.SessionId, r.Participants, r.QuoteId,
        r.UnitAmountMinor, r.TotalAmountMinor, r.Currency, r.StartsAtUtc, r.Status, r.Reason, r.CreatedAtUtc, r.Version);
    public async Task<ReservationPage> ListAsync(string caller, bool admin, string? userId, string? status, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 50 || (long)(page - 1) * pageSize > int.MaxValue) throw new ReservationProblem(400, "InvalidPagination");
        if (!admin && userId is not null && userId != caller) throw new ReservationProblem(403, "OwnerFilterForbidden");
        var query = db.Reservations.AsNoTracking().Where(r => admin ? userId == null || r.UserId == userId : r.UserId == caller);
        if (!string.IsNullOrEmpty(status)) query = query.Where(r => r.Status == status);
        var total = await query.CountAsync(ct); var rows = await query.OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(View).ToArray(), total, page, pageSize);
    }
    public async Task<ReservationView> DetailAsync(Guid id, string caller, bool admin, CancellationToken ct)
    {
        var reservation=await FindAsync(id,caller,admin,ct);
        var saga=await db.Sagas.AsNoTracking().SingleAsync(x=>x.ReservationId==id,ct);
        return View(reservation) with {Compensation=new(saga.RefundRequired,saga.RefundCompleted,saga.SeatsReleaseRequired,saga.SeatsReleased)};
    }
    private Task<Reservation?> QueryAsync(Guid id, string caller, bool admin, CancellationToken ct) => db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && (admin || r.UserId == caller), ct);
    private async Task<Reservation> FindAsync(Guid id, string caller, bool admin, CancellationToken ct) => await QueryAsync(id, caller, admin, ct) ?? throw new ReservationProblem(404, "ReservationNotFound");
    public async Task<TransitionView[]> TimelineAsync(Guid id, string caller, bool admin, CancellationToken ct)
    {
        await FindAsync(id, caller, admin, ct);
        var sagaId = await db.Sagas.Where(s => s.ReservationId == id).Select(s => s.Id).SingleAsync(ct);
        return await db.History.AsNoTracking().Where(h => h.SagaId == sagaId).OrderBy(h => h.OccurredAtUtc).ThenBy(h => h.Id)
            .Select(h => new TransitionView(h.From, h.To, h.Reason, h.OccurredAtUtc, h.MessageId)).ToArrayAsync(ct);
    }
}
