using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Reservations.Api.Application;

public sealed class SagaCommands(ReservationsDb db,SagaRecovery recovery,TimeProvider clock)
{
    public static SagaView View(ReservationSaga s)=>new(s.Id,s.ReservationId,s.State,s.DeadlineUtc,s.HoldId,s.PaymentOperationId,s.RefundOperationId,
        s.RefundRequired,s.RefundCompleted,s.SeatsReleaseRequired,s.SeatsReleased,s.PaymentResolved,s.CancellationRequested,s.FailureReason,s.Version);
    public async Task<IdempotencyRequest> CancelAsync(Guid id,string caller,bool admin,string key,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(key) || key.Length>128 || key.Any(char.IsControl))throw new ReservationProblem(400,"IdempotencyKeyRequired");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        // Key lock precedes Saga lock, including concurrent keys against different reservations.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"cancel/"+caller+"/"+key},0))",ct);
        var saga=(await db.Sagas.FromSqlInterpolated($"SELECT * FROM reservations.\"Sagas\" WHERE \"ReservationId\"={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()??throw new ReservationProblem(404,"ReservationNotFound");
        var reservation=await db.Reservations.SingleAsync(x=>x.Id==id,ct);
        if(!admin && reservation.UserId!=caller)throw new ReservationProblem(404,"ReservationNotFound");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("D"))));
        var existing=await db.Idempotency.SingleOrDefaultAsync(x=>x.UserId==caller && x.OperationName=="reservation-cancel" && x.Key==key,ct);
        if(existing is not null){if(existing.RequestHash!=hash)throw new ReservationProblem(409,"IdempotencyConflict");return existing;}
        if(!saga.CancellationRequested)
        {
            if(saga.State!="Confirmed" || reservation.StartsAtUtc<=clock.GetUtcNow())throw new ReservationProblem(409,"CancellationRequiresConfirmedBeforeDeparture");
            var from=saga.State;var cause=Guid.NewGuid();saga.CancellationRequested=true;
            await recovery.CompensateAsync(saga,reservation,"UserCancellation",cause,ct);recovery.Record(saga,reservation,from,cause,"CancellationRequestedBy "+caller);
        }
        var result=new IdempotencyRequest{UserId=caller,OperationName="reservation-cancel",Key=key,RequestHash=hash,ResourceId=id,
            Location=$"/api/reservations/v1/reservations/{id}",ResponseBody=JsonSerializer.Serialize(new ReservationAccepted(id,"CancellationPending",saga.Id),MessageCodec.Json)};
        db.Idempotency.Add(result);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
    }
    public async Task<SagaView> RetryAsync(Guid id,string actor,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var saga=(await db.Sagas.FromSqlInterpolated($"SELECT * FROM reservations.\"Sagas\" WHERE \"Id\"={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()??throw new ReservationProblem(404,"SagaNotFound");
        if(saga.State is not ("ManualReview" or "Compensating" or "CancellationPending"))throw new ReservationProblem(409,"CompensationNotPending");
        if(saga.PaymentRequested && !saga.PaymentResolved)throw new ReservationProblem(409,"ReconcileUncertainPaymentFirst");
        var reservation=await db.Reservations.SingleAsync(x=>x.Id==saga.ReservationId,ct);var from=saga.State;var cause=Guid.NewGuid();
        await recovery.CompensateAsync(saga,reservation,saga.FailureReason??"AdminRetry",cause,ct);
        // New business messages bypass previously processed Inbox entries, preserving every operation identifier.
        if(!saga.RefundCompleted && saga.RefundRequired)await recovery.IntentAsync(saga,new RefundPayment(saga.RefundOperationId,saga.PaymentOperationId,reservation.Id,reservation.UserId,reservation.TotalAmountMinor,reservation.Currency,saga.Version),$"refund-retry/{saga.Version}",cause,ct);
        if(!saga.SeatsReleased)await recovery.IntentAsync(saga,new ReleaseSeats(saga.HoldId,saga.FailureReason??"AdminRetry"),$"release-retry/{saga.Version}",cause,ct);
        recovery.Record(saga,reservation,from,cause,"CompensationRetryBy "+actor);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return View(saga);
    }
}
