using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservations.Api.Application;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Reservations.Api.Messaging;

public sealed class ReservationConsumer(ReservationsDb db, TimeProvider clock) : IInboxConsumer
{
    public async Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope, CancellationToken ct)
    {
        if (envelope.Type is not ("SeatsHeld" or "SeatsHoldRejected" or "TourSessionChanged" or "PaymentSucceeded" or "PaymentDeclined" or "PaymentOutcomeUnknown" or "SeatsConfirmed" or "SeatsConfirmationRejected" or "SeatsReleased")) throw new PoisonMessageException("ReservationsHandlerNotImplemented");
        await InboxTransaction.ProcessAsync(db, "reservations", "ReservationsWorkflow", envelope, async token =>
        {
            if (envelope.Type == "TourSessionChanged") { await ProjectAsync(MessageCodec.Payload<TourSessionChanged>(envelope), token); return; }
            var sagas = await db.Sagas.FromSqlInterpolated($"SELECT * FROM reservations.\"Sagas\" WHERE \"Id\"={envelope.SagaId!.Value} FOR UPDATE").ToListAsync(token);
            var saga = sagas.SingleOrDefault() ?? throw new PoisonMessageException("UnknownSaga");
            var reservation = await db.Reservations.SingleAsync(r => r.Id == saga.ReservationId, token);
            var from = saga.State; var duplicate = false; string? historyReason = null; var now = clock.GetUtcNow();
            switch (envelope.Type)
            {
                case "PaymentOutcomeUnknown":
                    var unknown = MessageCodec.Payload<PaymentOutcomeUnknown>(envelope);
                    if (unknown.PaymentOperationId != saga.PaymentOperationId || unknown.NextReconcileAtUtc == default || unknown.NextReconcileAtUtc.Offset != TimeSpan.Zero) throw new PoisonMessageException("InvalidUnknownOutcome");
                    duplicate = true; // uncertainty does not assert decline or abandon the hold in LAB-004
                    historyReason = "PaymentOutcomeUnknown; original provider lookup pending";
                    break;
                case "PaymentSucceeded":
                    var paid = MessageCodec.Payload<PaymentSucceeded>(envelope);
                    if (paid.PaymentOperationId != saga.PaymentOperationId || string.IsNullOrWhiteSpace(paid.ProviderReference) || paid.ProviderReference.Length > 200) throw new PoisonMessageException("InvalidPaymentOutcome");
                    if (saga.ProviderReference is not null)
                    {
                        if (saga.ProviderReference != paid.ProviderReference) throw new PoisonMessageException("ContradictoryProviderReference");
                        duplicate = true; break;
                    }
                    if (saga.State != "AwaitingPayment" || saga.HoldExpiresAtUtc is not DateTimeOffset expiry || expiry <= now) throw new PoisonMessageException("LatePaymentRequiresLAB005Repair");
                    saga.ProviderReference = paid.ProviderReference; saga.State = "AwaitingConfirmation"; saga.DeadlineUtc = now.AddSeconds(30);
                    ReservationIntents.Add(db, new ConfirmSeats(saga.HoldId), $"saga/{saga.Id}/confirm", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    break;
                case "PaymentDeclined":
                    var declined = MessageCodec.Payload<PaymentDeclined>(envelope);
                    if (declined.PaymentOperationId != saga.PaymentOperationId || string.IsNullOrWhiteSpace(declined.Reason) || declined.Reason.Length > 100) throw new PoisonMessageException("InvalidDecline");
                    if (saga.State is "Compensating" or "Failed" && saga.FailureReason == declined.Reason && !saga.RefundRequired) { duplicate = true; break; }
                    if (saga.State != "AwaitingPayment" || saga.ProviderReference is not null) throw new PoisonMessageException("ImpossibleDecline");
                    saga.State = "Compensating"; saga.FailureReason = declined.Reason; reservation.Reason = declined.Reason; saga.SeatsReleaseRequired = true;
                    ReservationIntents.Add(db, new ReleaseSeats(saga.HoldId, declined.Reason), $"saga/{saga.Id}/release", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    break;
                case "SeatsConfirmed":
                    if (MessageCodec.Payload<SeatsConfirmed>(envelope).HoldId != saga.HoldId) throw new PoisonMessageException("InvalidConfirmation");
                    if (saga.State == "Confirmed") { duplicate = true; break; }
                    if (saga.State != "AwaitingConfirmation" || saga.ProviderReference is null) throw new PoisonMessageException("ImpossibleConfirmation");
                    saga.State = "Confirmed";
                    ReservationIntents.Add(db, new ReservationConfirmed(reservation.Id, reservation.UserId, "Seats confirmed after a successful simulated payment."), $"saga/{saga.Id}/confirmed", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    break;
                case "SeatsConfirmationRejected":
                    var failed = MessageCodec.Payload<SeatsConfirmationRejected>(envelope);
                    if (failed.HoldId != saga.HoldId || string.IsNullOrWhiteSpace(failed.Reason) || failed.Reason.Length > 100) throw new PoisonMessageException("InvalidConfirmationRejection");
                    if (saga.State == "Compensating" && saga.RefundRequired && saga.FailureReason == failed.Reason) { duplicate = true; break; }
                    if (saga.State != "AwaitingConfirmation" || saga.ProviderReference is null) throw new PoisonMessageException("ImpossibleConfirmationRejection");
                    saga.State = "Compensating"; saga.FailureReason = failed.Reason; reservation.Reason = failed.Reason;
                    saga.RefundRequired = true; saga.SeatsReleaseRequired = true;
                    ReservationIntents.Add(db, new RefundPayment(saga.RefundOperationId, saga.PaymentOperationId, reservation.Id, reservation.UserId, reservation.TotalAmountMinor, reservation.Currency), $"saga/{saga.Id}/refund", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    ReservationIntents.Add(db, new ReleaseSeats(saga.HoldId, failed.Reason), $"saga/{saga.Id}/release", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    break;
                case "SeatsReleased":
                    if (MessageCodec.Payload<SeatsReleased>(envelope).HoldId != saga.HoldId) throw new PoisonMessageException("InvalidRelease");
                    if (saga.SeatsReleased) { duplicate = true; break; }
                    if (saga.State != "Compensating" || !saga.SeatsReleaseRequired) throw new PoisonMessageException("ImpossibleRelease");
                    saga.SeatsReleased = true;
                    if (!saga.RefundRequired || saga.RefundCompleted)
                    {
                        saga.State = "Failed";
                        ReservationIntents.Add(db, new ReservationFailed(reservation.Id, reservation.UserId, saga.FailureReason!), $"saga/{saga.Id}/failed", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    }
                    break;
                case "SeatsHeld":
                    var held = MessageCodec.Payload<SeatsHeld>(envelope);
                    if (held.HoldId != saga.HoldId || held.ExpiresAtUtc == default || held.ExpiresAtUtc.Offset != TimeSpan.Zero) throw new PoisonMessageException("InvalidHoldOutcome");
                    if (saga.State is "AwaitingPayment" or "AwaitingConfirmation" or "Confirmed" && saga.HoldExpiresAtUtc == held.ExpiresAtUtc) { duplicate = true; break; }
                    if (saga.State != "AwaitingAvailability") throw new PoisonMessageException("ImpossibleHoldTransition");
                    if (held.ExpiresAtUtc <= now) throw new PoisonMessageException("ExpiredHoldRequiresCompensation");
                    saga.State = "AwaitingPayment"; saga.HoldExpiresAtUtc = held.ExpiresAtUtc;
                    saga.DeadlineUtc = now.AddMinutes(2) < held.ExpiresAtUtc ? now.AddMinutes(2) : held.ExpiresAtUtc;
                    ReservationIntents.Add(db, new ProcessPayment(saga.PaymentOperationId, reservation.Id, reservation.UserId,
                        reservation.TotalAmountMinor, reservation.Currency), $"saga/{saga.Id}/payment", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    break;
                case "SeatsHoldRejected":
                    var rejected = MessageCodec.Payload<SeatsHoldRejected>(envelope);
                    if (rejected.HoldId != saga.HoldId || string.IsNullOrWhiteSpace(rejected.Reason) || rejected.Reason.Length > 100) throw new PoisonMessageException("InvalidHoldRejection");
                    if (saga.State == "Failed" && saga.FailureReason == rejected.Reason) { duplicate = true; break; }
                    if (saga.State != "AwaitingAvailability") throw new PoisonMessageException("ImpossibleRejectionTransition");
                    saga.State = "Failed"; saga.FailureReason = rejected.Reason; reservation.Reason = rejected.Reason;
                    ReservationIntents.Add(db, new ReservationFailed(reservation.Id, reservation.UserId, rejected.Reason), $"saga/{saga.Id}/failed", now, saga.Id, envelope.CorrelationId, envelope.MessageId);
                    break;
            }
            if (!duplicate)
            { saga.Version = checked(saga.Version + 1); reservation.Status = saga.State; reservation.Version = checked(reservation.Version + 1); }
            db.History.Add(new TransitionHistory { Id = Guid.NewGuid(), SagaId = saga.Id, MessageId = envelope.MessageId,
                From = from, To = saga.State, Reason = historyReason ?? (duplicate ? "CompatibleDuplicateIgnored" : saga.FailureReason), OccurredAtUtc = now });
        }, ct);
    }
    private async Task ProjectAsync(TourSessionChanged change, CancellationToken ct)
    {
        if (change.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(change.TourName) || change.TourName.Length > 200
            || change.PriceVersion <= 0 || change.UnitAmountMinor <= 0 || change.Currency != "MXN") throw new PoisonMessageException("InvalidProjection");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"projection/" + change.SessionId},0))", ct);
        var row = await db.Projections.SingleOrDefaultAsync(p => p.SessionId == change.SessionId, ct);
        if (row is not null && row.PriceVersion == change.PriceVersion && (row.Name != change.TourName || row.Active != change.Active || row.UnitAmountMinor != change.UnitAmountMinor || row.Currency != change.Currency))
            throw new PoisonMessageException("ProjectionVersionConflict");
        if (row is not null && row.PriceVersion >= change.PriceVersion) return;
        if (row is null) { row = new TourProjection { SessionId = change.SessionId }; db.Projections.Add(row); }
        row.Name = change.TourName; row.Active = change.Active; row.PriceVersion = change.PriceVersion;
        row.UnitAmountMinor = change.UnitAmountMinor; row.Currency = change.Currency; row.UpdatedAtUtc = clock.GetUtcNow();
    }
}
