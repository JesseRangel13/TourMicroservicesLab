using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Reservations.Api.Application;

public sealed class SagaRecoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int AvailabilitySeconds { get; set; } = 30;
    public int ConfirmationSeconds { get; set; } = 30;
    public int PaymentSeconds { get; set; } = 120;
    public int RecoverySeconds { get; set; } = 120;
}

// Called only while holding the Saga row lock in the caller's local transaction.
public sealed class SagaRecovery(ReservationsDb db,TimeProvider clock,SagaRecoveryOptions options)
{
    public async Task IntentAsync<T>(ReservationSaga saga,T payload,string effect,Guid cause,CancellationToken ct)
    {
        var key=$"saga/{saga.Id}/{effect}";
        if(db.Outbox.Local.Any(x=>x.EffectKey==key) || await db.Outbox.AnyAsync(x=>x.EffectKey==key,ct))return;
        ReservationIntents.Add(db,payload,key,clock.GetUtcNow(),saga.Id,saga.Id,cause);
    }
    public async Task ReleaseAsync(ReservationSaga saga,Guid cause,CancellationToken ct)
    {
        saga.SeatsReleaseRequired=true;
        if(!saga.SeatsReleased)await IntentAsync(saga,new ReleaseSeats(saga.HoldId,saga.FailureReason??"Abandoned"),"release",cause,ct);
    }
    public async Task CompensateAsync(ReservationSaga saga,Reservation reservation,string reason,Guid cause,CancellationToken ct)
    {
        saga.FailureReason=reason;reservation.Reason=reason;
        saga.State=saga.CancellationRequested?"CancellationPending":"Compensating";
        saga.DeadlineUtc=clock.GetUtcNow().AddSeconds(options.RecoverySeconds);
        if(saga.ProviderReference is not null)
        {
            saga.RefundRequired=true;
            if(!saga.RefundCompleted)await IntentAsync(saga,new RefundPayment(saga.RefundOperationId,saga.PaymentOperationId,reservation.Id,reservation.UserId,reservation.TotalAmountMinor,reservation.Currency),"refund",cause,ct);
        }
        await ReleaseAsync(saga,cause,ct);
        await CompleteAsync(saga,reservation,cause,ct);
    }
    public async Task UncertainAsync(ReservationSaga saga,Reservation reservation,string reason,Guid cause,CancellationToken ct)
    {
        saga.State="PaymentUncertain";saga.FailureReason=reason;reservation.Reason=reason;
        saga.DeadlineUtc=clock.GetUtcNow().AddSeconds(options.RecoverySeconds);
        await ReleaseAsync(saga,cause,ct);
        await IntentAsync(saga,new ReconcilePayment(saga.PaymentOperationId),"reconcile",cause,ct);
    }
    public async Task CompleteAsync(ReservationSaga saga,Reservation reservation,Guid cause,CancellationToken ct)
    {
        if(saga.State is not ("Compensating" or "CancellationPending" or "ManualReview") || !saga.SeatsReleaseRequired || !saga.SeatsReleased
            || saga.RefundRequired && !saga.RefundCompleted || saga.PaymentRequested && !saga.PaymentResolved)return;
        saga.State=saga.CancellationRequested?"Cancelled":"Failed";
        if(saga.CancellationRequested)await IntentAsync(saga,new ReservationCancelled(reservation.Id,reservation.UserId,"Full simulated refund and seat release completed."),"cancelled",cause,ct);
        else await IntentAsync(saga,new ReservationFailed(reservation.Id,reservation.UserId,saga.FailureReason??"Abandoned"),"failed",cause,ct);
    }
    public void Record(ReservationSaga saga,Reservation reservation,string from,Guid cause,string? reason=null)
    {
        saga.Version=checked(saga.Version+1);reservation.Version=checked(reservation.Version+1);reservation.Status=saga.State;reservation.Reason=saga.FailureReason;
        db.History.Add(new TransitionHistory{Id=Guid.NewGuid(),SagaId=saga.Id,MessageId=cause,From=from,To=saga.State,
            Reason=BoundedReason(reason??saga.FailureReason),OccurredAtUtc=clock.GetUtcNow()});
    }
    public async Task<bool> DueAsync(CancellationToken ct,Guid? selected=null)
    {
        db.ChangeTracker.Clear();await using var tx=await db.Database.BeginTransactionAsync(ct);var now=clock.GetUtcNow();
        var saga=(await db.Sagas.FromSqlInterpolated($"""
            SELECT * FROM reservations."Sagas" WHERE "State" IN ('AwaitingAvailability','AwaitingPayment','AwaitingConfirmation','PaymentUncertain','Compensating','CancellationPending')
            AND "DeadlineUtc"<={now} AND ({selected}::uuid IS NULL OR "Id"={selected}) ORDER BY "DeadlineUtc","Id" FOR UPDATE SKIP LOCKED LIMIT 1
            """).ToListAsync(ct)).SingleOrDefault();
        if(saga is null)return false;
        var reservation=await db.Reservations.SingleAsync(x=>x.Id==saga.ReservationId,ct);var from=saga.State;var cause=Guid.NewGuid();
        switch(from)
        {
            case "AwaitingAvailability":await CompensateAsync(saga,reservation,"AvailabilityDeadlineExceeded",cause,ct);break;
            case "AwaitingPayment":await UncertainAsync(saga,reservation,"PaymentDeadlineExceeded",cause,ct);break;
            case "AwaitingConfirmation":await CompensateAsync(saga,reservation,"ConfirmationDeadlineExceeded",cause,ct);break;
            default:saga.State="ManualReview";saga.FailureReason??=from=="PaymentUncertain"?"PaymentOutcomeUnresolved":"CompensationDeadlineExceeded";break;
        }
        Record(saga,reservation,from,cause,saga.State=="ManualReview"?"AutomaticRecoveryDeadlineExceeded":null);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
    private static string? BoundedReason(string? reason)=>reason is {Length:>100}?reason[..100]:reason;
}

public sealed class SagaDeadlineWorker(IServiceScopeFactory scopes,IOptions<SagaRecoveryOptions> options,ILogger<SagaDeadlineWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if(!options.Value.Enabled)return;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();using var operation=CancellationTokenSource.CreateLinkedTokenSource(stop);operation.CancelAfter(TimeSpan.FromSeconds(30));
                if(await scope.ServiceProvider.GetRequiredService<SagaRecovery>().DueAsync(operation.Token))continue;
            }
            catch(Exception error) when(!stop.IsCancellationRequested){logger.LogWarning("Durable Saga deadline work retained; {FailureType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(1),stop);
        }
    }
}
