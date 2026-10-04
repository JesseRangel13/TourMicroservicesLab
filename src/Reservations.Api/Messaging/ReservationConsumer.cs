using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservations.Api.Application;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Reservations.Api.Messaging;

public sealed class ReservationConsumer(ReservationsDb db, TimeProvider clock, SagaRecoveryOptions? options=null) : IInboxConsumer
{
    public async Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope,CancellationToken ct)
    {
        if(envelope.Type is not ("SeatsHeld" or "SeatsHoldRejected" or "TourSessionChanged" or "PaymentSucceeded" or "PaymentDeclined" or "PaymentOutcomeUnknown" or "SeatsConfirmed" or "SeatsConfirmationRejected" or "SeatsReleased" or "SeatsHoldExpired" or "PaymentRefunded" or "RefundNeedsReview"))throw new PoisonMessageException("ReservationsHandlerNotImplemented");
        await InboxTransaction.ProcessAsync(db,"reservations","ReservationsWorkflow",envelope,async token=>
        {
            if(envelope.Type=="TourSessionChanged"){await ProjectAsync(MessageCodec.Payload<TourSessionChanged>(envelope),token);return;}
            var saga=(await db.Sagas.FromSqlInterpolated($"SELECT * FROM reservations.\"Sagas\" WHERE \"Id\"={envelope.SagaId!.Value} FOR UPDATE").ToListAsync(token)).SingleOrDefault()??throw new PoisonMessageException("UnknownSaga");
            var reservation=await db.Reservations.SingleAsync(x=>x.Id==saga.ReservationId,token);
            var policy=options??new SagaRecoveryOptions();var recovery=new SagaRecovery(db,clock,policy);
            var from=saga.State;var cause=envelope.MessageId;var now=clock.GetUtcNow();var noOp=false;string? historyReason=null;
            switch(envelope.Type)
            {
                case "SeatsHeld":
                    var held=MessageCodec.Payload<SeatsHeld>(envelope);CheckHold(held.HoldId,saga);
                    if(held.ExpiresAtUtc==default || held.ExpiresAtUtc.Offset!=TimeSpan.Zero)throw new PoisonMessageException("InvalidHoldExpiry");
                    if(saga.HoldExpiresAtUtc is not null && saga.HoldExpiresAtUtc!=held.ExpiresAtUtc)throw new PoisonMessageException("ContradictoryHoldExpiry");
                    saga.HoldExpiresAtUtc=held.ExpiresAtUtc;
                    if(saga.State=="AwaitingAvailability" && saga.DeadlineUtc>now && held.ExpiresAtUtc>now)
                    {
                        saga.State="AwaitingPayment";saga.PaymentRequested=true;
                        saga.DeadlineUtc=now.AddSeconds(policy.PaymentSeconds)<held.ExpiresAtUtc?now.AddSeconds(policy.PaymentSeconds):held.ExpiresAtUtc;
                        await recovery.IntentAsync(saga,new ProcessPayment(saga.PaymentOperationId,reservation.Id,reservation.UserId,reservation.TotalAmountMinor,reservation.Currency),"payment",cause,token);
                    }
                    else if(saga.State=="AwaitingAvailability")await recovery.CompensateAsync(saga,reservation,"LateSeatsHeld",cause,token);
                    else if(saga.State is "AwaitingPayment" or "AwaitingConfirmation" or "Confirmed")noOp=true;
                    else
                    {
                        if(saga.State=="Failed" && !saga.SeatsReleaseRequired)throw new PoisonMessageException("HoldAfterDefinitiveRejection");
                        await recovery.ReleaseAsync(saga,cause,token);noOp=true;
                    }
                    break;
                case "SeatsHoldRejected":
                    var rejected=MessageCodec.Payload<SeatsHoldRejected>(envelope);CheckHold(rejected.HoldId,saga);CheckReason(rejected.Reason);
                    if(saga.State=="AwaitingAvailability")
                    {
                        saga.State="Failed";saga.FailureReason=rejected.Reason;
                        await recovery.IntentAsync(saga,new ReservationFailed(reservation.Id,reservation.UserId,rejected.Reason),"failed",cause,token);
                    }
                    else if(saga.SeatsReleaseRequired || saga.State=="Failed" && saga.FailureReason==rejected.Reason)noOp=true;
                    else throw new PoisonMessageException("ImpossibleHoldRejection");
                    break;
                case "PaymentOutcomeUnknown":
                    var unknown=MessageCodec.Payload<PaymentOutcomeUnknown>(envelope);CheckPayment(unknown.PaymentOperationId,saga);
                    if(unknown.NextReconcileAtUtc==default || unknown.NextReconcileAtUtc.Offset!=TimeSpan.Zero)throw new PoisonMessageException("InvalidUnknownOutcome");
                    if(!saga.PaymentRequested)throw new PoisonMessageException("UnexpectedPaymentOutcome");
                    noOp=true; // old uncertainty is compatible with a later definitive result
                    break;
                case "PaymentSucceeded":
                    var paid=MessageCodec.Payload<PaymentSucceeded>(envelope);CheckPayment(paid.PaymentOperationId,saga);
                    if(string.IsNullOrWhiteSpace(paid.ProviderReference) || paid.ProviderReference.Length>200)throw new PoisonMessageException("InvalidProviderReference");
                    if(!saga.PaymentRequested)throw new PoisonMessageException("UnexpectedPaymentOutcome");
                    if(saga.ProviderReference is not null && saga.ProviderReference!=paid.ProviderReference)throw new PoisonMessageException("ContradictoryProviderReference");
                    if(saga.ProviderReference is not null){noOp=true;break;}
                    // Late discovery can repair a previous definitive failure; it cannot confirm abandoned seats.
                    saga.ProviderReference=paid.ProviderReference;saga.PaymentResolved=true;
                    if(saga.State=="AwaitingPayment" && saga.DeadlineUtc>now && saga.HoldExpiresAtUtc>now && !saga.SeatsReleaseRequired)
                    {
                        saga.State="AwaitingConfirmation";saga.DeadlineUtc=now.AddSeconds(policy.ConfirmationSeconds);
                        await recovery.IntentAsync(saga,new ConfirmSeats(saga.HoldId),"confirm",cause,token);
                    }
                    else await recovery.CompensateAsync(saga,reservation,"LatePaymentSucceeded",cause,token);
                    break;
                case "PaymentDeclined":
                    var declined=MessageCodec.Payload<PaymentDeclined>(envelope);CheckPayment(declined.PaymentOperationId,saga);CheckReason(declined.Reason);
                    if(!saga.PaymentRequested || saga.ProviderReference is not null)throw new PoisonMessageException("ContradictoryDecline");
                    if(saga.PaymentResolved){if(saga.FailureReason!=declined.Reason)throw new PoisonMessageException("ContradictoryDeclineReason");noOp=true;break;}
                    saga.PaymentResolved=true;await recovery.CompensateAsync(saga,reservation,declined.Reason,cause,token);break;
                case "SeatsConfirmed":
                    CheckHold(MessageCodec.Payload<SeatsConfirmed>(envelope).HoldId,saga);
                    if(saga.State=="Confirmed"){noOp=true;break;}
                    if(saga.State=="AwaitingConfirmation" && saga.DeadlineUtc>now && !saga.SeatsReleaseRequired && saga.ProviderReference is not null)
                    {
                        saga.State="Confirmed";await recovery.IntentAsync(saga,new ReservationConfirmed(reservation.Id,reservation.UserId,"Seats confirmed after a successful simulated payment."),"confirmed",cause,token);
                    }
                    else if(saga.ProviderReference is not null)
                    {
                        if(saga.State is "Cancelled" or "Failed" && saga.RefundCompleted && saga.SeatsReleased)noOp=true;
                        else await recovery.CompensateAsync(saga,reservation,saga.FailureReason??"LateSeatsConfirmed",cause,token);
                    }
                    else throw new PoisonMessageException("ImpossibleConfirmation");
                    break;
                case "SeatsConfirmationRejected":
                    var failed=MessageCodec.Payload<SeatsConfirmationRejected>(envelope);CheckHold(failed.HoldId,saga);CheckReason(failed.Reason);
                    if(saga.ProviderReference is null)throw new PoisonMessageException("ImpossibleConfirmationRejection");
                    if(saga.SeatsReleaseRequired){noOp=true;break;}
                    if(saga.State!="AwaitingConfirmation")throw new PoisonMessageException("ImpossibleConfirmationRejection");
                    await recovery.CompensateAsync(saga,reservation,failed.Reason,cause,token);break;
                case "SeatsHoldExpired":
                    CheckHold(MessageCodec.Payload<SeatsHoldExpired>(envelope).HoldId,saga);
                    if(saga.State=="Confirmed")throw new PoisonMessageException("ExpiredConfirmedHold");
                    if(saga.SeatsReleaseRequired){noOp=true;break;}
                    if(saga.ProviderReference is not null)await recovery.CompensateAsync(saga,reservation,"HoldExpired",cause,token);
                    else if(saga.PaymentRequested && !saga.PaymentResolved)await recovery.UncertainAsync(saga,reservation,"HoldExpired",cause,token);
                    else await recovery.CompensateAsync(saga,reservation,"HoldExpired",cause,token);
                    break;
                case "SeatsReleased":
                    CheckHold(MessageCodec.Payload<SeatsReleased>(envelope).HoldId,saga);
                    if(!saga.SeatsReleaseRequired)throw new PoisonMessageException("UnexpectedRelease");
                    if(saga.SeatsReleased){noOp=true;break;}
                    saga.SeatsReleased=true;await recovery.CompleteAsync(saga,reservation,cause,token);break;
                case "PaymentRefunded":
                    var refund=MessageCodec.Payload<PaymentRefunded>(envelope);CheckPayment(refund.PaymentOperationId,saga);
                    if(refund.RefundOperationId!=saga.RefundOperationId || !saga.RefundRequired)throw new PoisonMessageException("UnexpectedRefund");
                    if(saga.RefundCompleted){noOp=true;break;}
                    saga.RefundCompleted=true;await recovery.CompleteAsync(saga,reservation,cause,token);break;
                case "RefundNeedsReview":
                    var review=MessageCodec.Payload<RefundNeedsReview>(envelope);CheckReason(review.Reason);
                    if(review.RefundOperationId!=saga.RefundOperationId || !saga.RefundRequired)throw new PoisonMessageException("UnexpectedRefundReview");
                    if(saga.RefundCompleted){noOp=true;break;}
                    saga.State="ManualReview";saga.FailureReason??=review.Reason;historyReason=review.Reason;break;
            }
            recovery.Record(saga,reservation,from,cause,noOp?"CompatibleStaleResultIgnored":historyReason);
        },ct);
    }
    private static void CheckHold(Guid id,ReservationSaga saga){if(id!=saga.HoldId)throw new PoisonMessageException("ContradictoryHoldId");}
    private static void CheckPayment(Guid id,ReservationSaga saga){if(id!=saga.PaymentOperationId)throw new PoisonMessageException("ContradictoryPaymentId");}
    private static void CheckReason(string reason){if(string.IsNullOrWhiteSpace(reason) || reason.Length>100)throw new PoisonMessageException("InvalidReason");}
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
