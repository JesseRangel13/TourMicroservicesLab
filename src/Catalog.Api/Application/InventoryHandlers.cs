using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Catalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
namespace Catalog.Api.Application;

public sealed record CatalogMessageContext(Guid MessageId, Guid SagaId, Guid CorrelationId)
{
    public void Validate() => CatalogRules.Require(MessageId != Guid.Empty && SagaId != Guid.Empty && CorrelationId != Guid.Empty);
}
public sealed record InventoryResult(bool Succeeded, string Status, string? Reason = null, DateTimeOffset? ExpiresAtUtc = null);
public sealed class InventoryHandlers(CatalogDb db, TimeProvider clock)
{
    public async Task<InventoryResult> HoldAsync(HoldSeats command, CatalogMessageContext context, CancellationToken ct)
    {
        context.Validate(); CatalogRules.Require(command.HoldId != Guid.Empty && command.QuoteId != Guid.Empty
            && command.ReservationId != Guid.Empty && !string.IsNullOrWhiteSpace(command.UserId) && command.UserId.Length <= 128);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CatalogRules.LockAsync(db, $"hold/{command.HoldId}", ct);
        var hold = await db.Holds.SingleOrDefaultAsync(h => h.Id == command.HoldId, ct);
        if (hold is not null)
        {
            CatalogRules.Require(hold.SagaId == context.SagaId && (hold.RequestFingerprint is null || hold.RequestFingerprint == fingerprint), "OperationIdentityConflict", 409);
            if (hold.Status is "Held" or "Confirmed")
            {
                await HeldIntentAsync(hold, context, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return new(true, hold.Status, ExpiresAtUtc: hold.ExpiresAtUtc);
            }
            return await RejectAsync(hold, hold.Reason ?? "HoldAlreadyReleased", context, tx, ct);
        }
        hold = new SeatHold { Id = command.HoldId, SagaId = context.SagaId, RequestFingerprint = fingerprint,
            CreatedAtUtc = clock.GetUtcNow(), Status = "Rejected" };
        db.Holds.Add(hold);
        await CatalogRules.LockAsync(db, $"quote-use/{command.QuoteId}", ct);
        var quote = await db.Quotes.SingleOrDefaultAsync(q => q.Id == command.QuoteId, ct);
        if (quote is null || quote.UserId != command.UserId) return await RejectAsync(hold, "InvalidQuote", context, tx, ct);
        if (await db.Holds.AnyAsync(h => h.QuoteId == quote.Id, ct)) return await RejectAsync(hold, "QuoteAlreadyUsed", context, tx, ct);
        var tourId = await db.Sessions.Where(s => s.Id == quote.SessionId).Select(s => s.TourId).SingleAsync(ct);
        var tour = await CatalogRules.TourAsync(db, tourId, ct);
        var session = await CatalogRules.SessionAsync(db, quote.SessionId, ct);
        var now = clock.GetUtcNow();
        if (quote.ExpiresAtUtc <= now) return await RejectAsync(hold, "QuoteExpired", context, tx, ct);
        if (!tour.Active || session.StartsAtUtc <= now) return await RejectAsync(hold, "SessionUnavailable", context, tx, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog."Sessions" SET "AvailableSeats"="AvailableSeats"-{quote.Participants}, "Version"="Version"+1
            WHERE "Id"={session.Id} AND "AvailableSeats">={quote.Participants}
            """, ct);
        if (changed == 0) return await RejectAsync(hold, "InsufficientSeats", context, tx, ct);
        // Session is not saved through EF after the conditional SQL; its tracked values are now stale.
        db.Entry(session).State = EntityState.Detached;
        hold.Status = "Held"; hold.ReservationId = command.ReservationId; hold.QuoteId = quote.Id; hold.SessionId = quote.SessionId;
        hold.UserId = command.UserId; hold.Participants = quote.Participants; hold.ExpiresAtUtc = now.AddMinutes(10);
        await HeldIntentAsync(hold, context, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(true, "Held", ExpiresAtUtc: hold.ExpiresAtUtc);
    }
    public async Task<InventoryResult> ConfirmAsync(ConfirmSeats command, CatalogMessageContext context, CancellationToken ct)
    {
        context.Validate(); CatalogRules.Require(command.HoldId != Guid.Empty);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CatalogRules.LockAsync(db, $"hold/{command.HoldId}", ct);
        var hold = await db.Holds.SingleOrDefaultAsync(h => h.Id == command.HoldId, ct);
        if (hold is not null) CatalogRules.Require(hold.SagaId == context.SagaId, "OperationIdentityConflict", 409);
        if (hold?.SessionId is Guid sessionId) await CatalogRules.SessionAsync(db, sessionId, ct);
        var now = clock.GetUtcNow();
        if (hold is not null && (hold.Status == "Confirmed" || (hold.Status == "Held" && hold.ExpiresAtUtc > now)))
        {
            if (hold.Status == "Held") { hold.Status = "Confirmed"; hold.Version = checked(hold.Version + 1); }
            await IntentAsync($"hold/{hold.Id}/confirmed", new SeatsConfirmed(hold.Id), context, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, "Confirmed");
        }
        var reason = hold is null ? "HoldNotFound" : hold.Status == "Held" ? "HoldExpired" : "HoldNotHeld";
        await IntentAsync($"hold/{command.HoldId}/confirmation-rejected", new SeatsConfirmationRejected(command.HoldId, reason), context, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(false, hold?.Status ?? "Missing", reason);
    }
    public async Task<InventoryResult> ReleaseAsync(ReleaseSeats command, CatalogMessageContext context, CancellationToken ct)
    {
        context.Validate(); CatalogRules.Require(command.HoldId != Guid.Empty && !string.IsNullOrWhiteSpace(command.Reason) && command.Reason.Length <= 100);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CatalogRules.LockAsync(db, $"hold/{command.HoldId}", ct);
        var hold = await db.Holds.SingleOrDefaultAsync(h => h.Id == command.HoldId, ct);
        if (hold is null)
        {
            hold = new SeatHold { Id = command.HoldId, SagaId = context.SagaId, Status = "Released", Reason = command.Reason, CreatedAtUtc = clock.GetUtcNow() };
            db.Holds.Add(hold);
        }
        else
        {
            CatalogRules.Require(hold.SagaId == context.SagaId, "OperationIdentityConflict", 409);
            if (hold.SessionId is Guid sessionId)
            {
                var session = await CatalogRules.SessionAsync(db, sessionId, ct);
                if (hold.Status is "Held" or "Confirmed")
                {
                    session.AvailableSeats = checked(session.AvailableSeats + hold.Participants); session.Version = checked(session.Version + 1);
                    hold.Status = "Released"; hold.Reason = command.Reason; hold.Version = checked(hold.Version + 1);
                }
            }
            else if (hold.Status == "Rejected") { hold.Status = "Released"; hold.Version = checked(hold.Version + 1); }
        }
        await IntentAsync($"hold/{hold.Id}/released", new SeatsReleased(hold.Id), context, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, hold.Status);
    }
    // Directly callable by tests; scheduling and transport are deliberately deferred to LAB-003.
    public async Task<InventoryResult> ExpireAsync(Guid holdId, CancellationToken ct)
    {
        CatalogRules.Require(holdId != Guid.Empty);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CatalogRules.LockAsync(db, $"hold/{holdId}", ct);
        var hold = await db.Holds.SingleOrDefaultAsync(h => h.Id == holdId, ct);
        if (hold?.SessionId is Guid sessionId)
        {
            var session = await CatalogRules.SessionAsync(db, sessionId, ct);
            if (hold.Status == "Held" && hold.ExpiresAtUtc <= clock.GetUtcNow())
            {
                session.AvailableSeats = checked(session.AvailableSeats + hold.Participants); session.Version = checked(session.Version + 1);
                hold.Status = "Expired"; hold.Reason = "HoldExpired"; hold.Version = checked(hold.Version + 1);
                await IntentAsync($"hold/{hold.Id}/expired", new SeatsHoldExpired(hold.Id), new(Guid.NewGuid(), hold.SagaId, hold.SagaId), ct);
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, hold?.Status ?? "Missing");
    }
    private Task HeldIntentAsync(SeatHold hold, CatalogMessageContext context, CancellationToken ct) =>
        IntentAsync($"hold/{hold.Id}/held", new SeatsHeld(hold.Id, hold.ExpiresAtUtc!.Value), context, ct);
    private Task IntentAsync<T>(string key, T payload, CatalogMessageContext context, CancellationToken ct) =>
        CatalogRules.AddIntentAsync(db, key, payload, clock.GetUtcNow(), context.SagaId, context.CorrelationId, context.MessageId, ct);
    private async Task<InventoryResult> RejectAsync(SeatHold hold, string reason, CatalogMessageContext context,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        if (hold.Status == "Rejected") hold.Reason = reason;
        await IntentAsync($"hold/{hold.Id}/hold-rejected", new SeatsHoldRejected(hold.Id, reason), context, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(false, hold.Status, reason);
    }
}
