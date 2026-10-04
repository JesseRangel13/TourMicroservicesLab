using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Payments.Api.Application;

public sealed class SimulatedRefundFailure : Exception;
public static class ReconciliationPolicy
{
    public static readonly int[] Intervals = [5,10,20,30,30];
}

public static class RefundAcceptance
{
    public static async Task AcceptAsync(PaymentsDb db,IntegrationEnvelope<JsonElement> envelope,CancellationToken ct)
    {
        var p=MessageCodec.Payload<RefundPayment>(envelope);
        if(p.RefundOperationId==Guid.Empty || p.PaymentOperationId==Guid.Empty || p.ReservationId==Guid.Empty || string.IsNullOrWhiteSpace(p.UserId)
            || p.AmountMinor<=0 || p.Currency!="MXN" || p.RetryVersion<0)throw new PoisonMessageException("InvalidRefund");
        await InboxTransaction.ProcessAsync(db,"payments","PaymentsAcceptance",envelope,async token=>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"payment/"+p.PaymentOperationId},0))",token);
            var original=await db.Operations.SingleOrDefaultAsync(x=>x.Id==p.PaymentOperationId,token);
            if(original is null || original.Status!="Succeeded" || original.SagaId!=envelope.SagaId || original.ReservationId!=p.ReservationId
                || original.UserId!=p.UserId || original.AmountMinor!=p.AmountMinor || original.Currency!=p.Currency)
                throw new PoisonMessageException("RefundChargeConflict");
            var existing=await db.Refunds.SingleOrDefaultAsync(x=>x.Id==p.RefundOperationId || x.PaymentOperationId==p.PaymentOperationId,token);
            if(existing is not null)
            {
                if(existing.Id!=p.RefundOperationId || existing.PaymentOperationId!=p.PaymentOperationId || existing.SagaId!=envelope.SagaId
                    || existing.ReservationId!=p.ReservationId || existing.UserId!=p.UserId || existing.AmountMinor!=p.AmountMinor || existing.Currency!=p.Currency)
                    throw new PoisonMessageException("RefundIdentityConflict");
                // A new MessageId alone cannot reopen an exhausted retry window. Admin Saga retry has a monotonic version.
                if(p.RetryVersion>existing.LastRetryVersion)
                {
                    existing.LastRetryVersion=p.RetryVersion;existing.Version++;
                    if(existing.Status=="ManualReview") {existing.Status="Pending";existing.Attempts=0;existing.NextAttemptAtUtc=null;}
                }
                if(existing.Status=="Refunded")
                {
                    var response=await db.Outbox.SingleAsync(x=>x.EffectKey==$"refund/{existing.Id}/refunded",token);
                    response.PublishedAtUtc=null;response.LeaseOwner=null;response.LeaseUntilUtc=null;
                }
                return;
            }
            var fault=(await db.Faults.FromSqlRaw("SELECT * FROM payments.\"Faults\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(token)).Single();
            var mode=original.Mode=="RefundTransientFailure"?original.Mode:fault.Remaining>0 && fault.Mode=="RefundTransientFailure"?fault.Mode:"Success";
            if(mode=="RefundTransientFailure" && original.Mode!=mode)fault.Remaining--;
            db.Refunds.Add(new RefundOperation{Id=p.RefundOperationId,PaymentOperationId=p.PaymentOperationId,SagaId=original.SagaId,
                ReservationId=p.ReservationId,UserId=p.UserId,AmountMinor=p.AmountMinor,Currency=p.Currency,Mode=mode,
                SourceMessageId=envelope.MessageId,CorrelationId=envelope.CorrelationId,LastRetryVersion=p.RetryVersion});
        },ct);
    }
}

public sealed class RefundProcessor(PaymentsDb db,IPaymentProvider provider,TimeProvider clock)
{
    public async Task<bool> ProcessAsync(CancellationToken ct,Guid? selected=null,Func<CancellationToken,Task>? afterProvider=null)
    {
        db.ChangeTracker.Clear();RefundOperation? operation;var now=clock.GetUtcNow();
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            operation=(await db.Refunds.FromSqlInterpolated($"""
                SELECT * FROM payments."RefundOperations" WHERE "Status"='Pending'
                AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc"<={now})
                AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc"<={now})
                AND ({selected}::uuid IS NULL OR "Id"={selected}) ORDER BY "Id" FOR UPDATE SKIP LOCKED LIMIT 1
                """).ToListAsync(ct)).SingleOrDefault();
            if(operation is null)return false;
            operation.LeaseOwner=Guid.NewGuid();operation.LeaseUntilUtc=now.AddSeconds(60);operation.Version++;
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        using var span=Shared.Infrastructure.LabTelemetry.Activities.StartActivity("handler.durable-work");
        span?.SetTag("ReservationId",operation.ReservationId);span?.SetTag("SagaId",operation.SagaId);span?.SetTag("OperationId",operation.Id);
        var owner=operation.LeaseOwner;string? reference=null;
        try
        {
            reference=await provider.GetRefundStatusAsync(operation.Id,ct);
            if(reference is null)
            {
                await provider.RefundAsync(new(operation.Id,operation.PaymentOperationId,operation.ReservationId,operation.UserId,operation.AmountMinor,operation.Currency),ct);
                reference=await provider.GetRefundStatusAsync(operation.Id,ct);
            }
        }
        catch(Exception e) when(e is TimeoutException or SimulatedRefundFailure) { /* Pending is uncertain; lookup precedes every retry. */ }
        if(afterProvider is not null)await afterProvider(ct);
        db.ChangeTracker.Clear();await using var commit=await db.Database.BeginTransactionAsync(ct);
        var row=(await db.Refunds.FromSqlInterpolated($"SELECT * FROM payments.\"RefundOperations\" WHERE \"Id\"={operation.Id} FOR UPDATE").ToListAsync(ct)).Single();
        if(row.LeaseOwner!=owner || row.LeaseUntilUtc<=clock.GetUtcNow())return false;
        row.Attempts++;row.Version++;row.LeaseOwner=null;row.LeaseUntilUtc=null;
        if(reference is not null)
        {
            row.Status="Refunded";row.ProviderReference=reference;row.Reason=null;row.NextAttemptAtUtc=null;
            await IntentAsync(new PaymentRefunded(row.Id,row.PaymentOperationId),row,"refunded",ct);
        }
        else if(row.Attempts>=5)
        {
            row.Status="ManualReview";row.Reason="RefundOutcomeUnresolved";row.NextAttemptAtUtc=null;
            await IntentAsync(new RefundNeedsReview(row.Id,row.Reason),row,"review",ct);
        }
        else {row.Reason="Refund outcome pending; status lookup required.";row.NextAttemptAtUtc=clock.GetUtcNow().AddSeconds(ReconciliationPolicy.Intervals[row.Attempts]);}
        await db.SaveChangesAsync(ct);await commit.CommitAsync(ct);Shared.Infrastructure.LabTelemetry.Count("durable_work",row.Status);return true;
    }
    private async Task IntentAsync<T>(T payload,RefundOperation row,string outcome,CancellationToken ct)
    {
        var key=$"refund/{row.Id}/{outcome}"+(outcome=="review" && row.LastRetryVersion>0?$"/{row.LastRetryVersion}":"");
        if(await db.Outbox.AnyAsync(x=>x.EffectKey==key,ct))return;
        var message=Guid.NewGuid();var delivery=Guid.NewGuid();var now=clock.GetUtcNow();
        db.Outbox.Add(new PaymentOutbox{DeliveryId=delivery,MessageId=message,EffectKey=key,Destination="tourlab-reservations",Type=typeof(T).Name,
            OccurredAtUtc=now,EnvelopeJson=MessageRoutes.Serialize(payload,message,delivery,now,row.SagaId,row.CorrelationId,row.SourceMessageId)});
    }
}
