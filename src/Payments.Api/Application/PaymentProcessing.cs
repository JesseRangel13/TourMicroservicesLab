using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Payments.Api.Application;

public sealed record ProviderCharge(Guid Id, string Status, string? Reference);
public interface IPaymentProvider
{
    Task<ProviderCharge> ChargeAsync(PaymentOperation operation, CancellationToken ct);
    Task<ProviderCharge?> GetChargeStatusAsync(Guid operationId, CancellationToken ct);
    Task RefundAsync(RefundPayment request, CancellationToken ct);
    Task<string?> GetRefundStatusAsync(Guid operationId, CancellationToken ct);
}
public sealed class DurableFakeProvider(IDbContextFactory<PaymentsDb> factory, TimeProvider clock) : IPaymentProvider
{
    public async Task<ProviderCharge> ChargeAsync(PaymentOperation operation, CancellationToken ct)
    {
        if (operation.Id == Guid.Empty || operation.SagaId == Guid.Empty || operation.ReservationId == Guid.Empty || string.IsNullOrWhiteSpace(operation.UserId)
            || operation.UserId.Length>128 || operation.AmountMinor <= 0 || operation.Currency != "MXN" || operation.Mode is not ("Success" or "Decline" or "TimeoutAfterCharge" or "UnknownUntilAdminResolution" or "RefundTransientFailure"))
            throw new PoisonMessageException("InvalidSimulatedProviderRequest");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"provider/" + operation.Id},0))", ct);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { operation.Id, operation.SagaId, operation.ReservationId, operation.UserId, operation.AmountMinor, operation.Currency, operation.Mode })));
        var row = await db.Effects.SingleOrDefaultAsync(e=>e.Id==operation.Id,ct);
        if (row is not null && row.RequestHash != hash) throw new PoisonMessageException("ProviderIdentityConflict");
        if (row is null)
        {
            row = new ProviderEffect {Id=operation.Id, RequestHash=hash,Mode=operation.Mode,
                Status=operation.Mode=="UnknownUntilAdminResolution"?"Unknown":operation.Mode=="Decline"?"Declined":"Succeeded", Reference=operation.Mode is "Decline" or "UnknownUntilAdminResolution"?null:"SIM-"+operation.Id.ToString("N"),CreatedAtUtc=clock.GetUtcNow()};
            db.Effects.Add(row);
        }
        var timeout = row.Mode=="TimeoutAfterCharge" && !row.TimeoutDelivered;
        if (timeout) row.TimeoutDelivered=true;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); // independent simulated effect survives the caller's crash
        if (timeout) throw new TimeoutException("Simulated timeout after durable charge.");
        return new(row.Id,row.Status,row.Reference);
    }
    public async Task<ProviderCharge?> GetChargeStatusAsync(Guid operationId, CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        return await db.Effects.AsNoTracking().Where(e=>e.Id==operationId).Select(e=>new ProviderCharge(e.Id,e.Status,e.Reference)).SingleOrDefaultAsync(ct);
    }
    public async Task RefundAsync(RefundPayment request,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"provider/"+request.PaymentOperationId},0))",ct);
        var original=await db.Operations.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.PaymentOperationId,ct);
        var charge=await db.Effects.SingleOrDefaultAsync(x=>x.Id==request.PaymentOperationId,ct);
        if(original is null || charge?.Status!="Succeeded" || original.ReservationId!=request.ReservationId || original.UserId!=request.UserId
            || original.AmountMinor!=request.AmountMinor || request.AmountMinor<=0 || original.Currency!=request.Currency)
            throw new PoisonMessageException("InvalidOriginalChargeForRefund");
        // Retry metadata is not part of the money effect; preserve the original six-field fingerprint across upgrades.
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {request.RefundOperationId,request.PaymentOperationId,request.ReservationId,request.UserId,request.AmountMinor,request.Currency})));
        var effect=await db.ProviderRefunds.SingleOrDefaultAsync(x=>x.PaymentOperationId==request.PaymentOperationId,ct);
        if(effect is not null && (effect.Id!=request.RefundOperationId || effect.RequestHash!=hash)) throw new PoisonMessageException("ProviderRefundIdentityConflict");
        if(effect is null) {effect=new ProviderRefund{Id=request.RefundOperationId,PaymentOperationId=request.PaymentOperationId,RequestHash=hash};db.ProviderRefunds.Add(effect);}
        var operation=await db.Refunds.AsNoTracking().SingleAsync(x=>x.Id==request.RefundOperationId,ct);
        var fail=effect.Reference is null && operation.Mode=="RefundTransientFailure" && effect.Failures<2;
        if(fail)effect.Failures++;else effect.Reference??="SIM-REFUND-"+effect.Id.ToString("N");
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        if(fail)throw new SimulatedRefundFailure();
    }
    public async Task<string?> GetRefundStatusAsync(Guid operationId,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        return await db.ProviderRefunds.Where(x=>x.Id==operationId).Select(x=>x.Reference).SingleOrDefaultAsync(ct);
    }
}
public sealed class PaymentConsumer(PaymentsDb db) : IInboxConsumer
{
    public async Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope,CancellationToken ct)
    {
        if(envelope.Type=="RefundPayment") {await RefundAcceptance.AcceptAsync(db,envelope,ct);return;}
        if(envelope.Type=="ReconcilePayment")
        {
            var reconcile=MessageCodec.Payload<ReconcilePayment>(envelope);
            await InboxTransaction.ProcessAsync(db,"payments","PaymentsAcceptance",envelope,async token=>
            {
                var row=(await db.Operations.FromSqlInterpolated($"SELECT * FROM payments.\"Operations\" WHERE \"Id\"={reconcile.PaymentOperationId} FOR UPDATE").ToListAsync(token)).SingleOrDefault();
                if(row is null || row.SagaId!=envelope.SagaId)throw new PoisonMessageException("InvalidReconciliationIdentity");
                if(row.Status=="Unknown" && row.ReconcileAttempts<5) {row.NextReconcileAtUtc=DateTimeOffset.UtcNow;row.Version++;}
            },ct);return;
        }
        if(envelope.Type!="ProcessPayment") throw new PoisonMessageException("PaymentHandlerNotImplemented");
        var payload=MessageCodec.Payload<ProcessPayment>(envelope);
        if(payload.PaymentOperationId==Guid.Empty || payload.ReservationId==Guid.Empty || string.IsNullOrWhiteSpace(payload.UserId)
            || payload.UserId.Length>128 || payload.AmountMinor<=0 || payload.Currency!="MXN") throw new PoisonMessageException("InvalidPayment");
        await InboxTransaction.ProcessAsync(db,"payments","PaymentsAcceptance",envelope,async token=>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"payment/" + payload.PaymentOperationId},0))",token);
            var existing=await db.Operations.SingleOrDefaultAsync(o=>o.Id==payload.PaymentOperationId,token);
            if(existing is not null)
            {
                if(existing.SagaId!=envelope.SagaId || existing.ReservationId!=payload.ReservationId || existing.UserId!=payload.UserId
                    || existing.AmountMinor!=payload.AmountMinor || existing.Currency!=payload.Currency) throw new PoisonMessageException("PaymentIdentityConflict");
                return;
            }
            var fault=(await db.Faults.FromSqlRaw("SELECT * FROM payments.\"Faults\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(token)).Single();
            var mode=fault.Remaining>0?fault.Mode:"Success"; if(fault.Remaining>0) fault.Remaining--;
            db.Operations.Add(new PaymentOperation {Id=payload.PaymentOperationId,SagaId=envelope.SagaId!.Value,ReservationId=payload.ReservationId,
                UserId=payload.UserId,AmountMinor=payload.AmountMinor,Currency=payload.Currency,Mode=mode,SourceMessageId=envelope.MessageId,CorrelationId=envelope.CorrelationId});
        },ct);
    }
}
public sealed class PaymentProcessor(PaymentsDb db, IPaymentProvider provider,TimeProvider clock,Microsoft.Extensions.Options.IOptions<PaymentWorkOptions>? options=null)
{
    public async Task<bool> ProcessAsync(CancellationToken ct,Guid? selected=null,Func<CancellationToken,Task>? afterProvider=null)
    {
        db.ChangeTracker.Clear();
        PaymentOperation? operation;
        var now=clock.GetUtcNow();
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            var rows=await db.Operations.FromSqlInterpolated($"""
                SELECT * FROM payments."Operations" WHERE "Status" IN ('Pending','Unknown')
                AND ("NextReconcileAtUtc" IS NULL OR "NextReconcileAtUtc"<={now})
                AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc"<={now})
                AND ({selected}::uuid IS NULL OR "Id"={selected})
                ORDER BY "Id" FOR UPDATE SKIP LOCKED LIMIT 1
                """).ToListAsync(ct);
            operation=rows.SingleOrDefault(); if(operation is null) return false;
            operation.LeaseOwner=Guid.NewGuid(); operation.LeaseUntilUtc=clock.GetUtcNow().AddSeconds(60);
            operation.Version++; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        var owner=operation.LeaseOwner; ProviderCharge? result=null; var unknown=false;
        try
        {
            result=await provider.GetChargeStatusAsync(operation.Id,ct);
            if(result is null && operation.Status=="Pending") result=await provider.ChargeAsync(operation,ct);
            unknown=result is null || result.Status=="Unknown";
        }
        catch(TimeoutException) { unknown=true; }
        if(afterProvider is not null) await afterProvider(ct); // controlled test barrier, no HTTP exposure
        db.ChangeTracker.Clear();
        await using var commit=await db.Database.BeginTransactionAsync(ct);
        var current=(await db.Operations.FromSqlInterpolated($"SELECT * FROM payments.\"Operations\" WHERE \"Id\"={operation.Id} FOR UPDATE").ToListAsync(ct)).Single();
        if(current.LeaseOwner!=owner || current.LeaseUntilUtc<=clock.GetUtcNow()) return false;
        if(unknown)
        {
            var first=current.Status!="Unknown"; current.Status="Unknown"; if(!first)current.ReconcileAttempts++;
            var intervals=options?.Value.ReconcileIntervalsSeconds??ReconciliationPolicy.Intervals;
            current.NextReconcileAtUtc=current.ReconcileAttempts<5?clock.GetUtcNow().AddSeconds(intervals[current.ReconcileAttempts]):DateTimeOffset.MaxValue;
            current.Reason="Provider outcome unresolved; no new charge is initiated.";
            if(first) AddIntent(new PaymentOutcomeUnknown(current.Id,current.NextReconcileAtUtc.Value),current,"unknown");
        }
        else
        {
            if(result!.Id!=current.Id || result.Status is not ("Succeeded" or "Declined" or "NotCharged") || result.Status=="Succeeded" && string.IsNullOrWhiteSpace(result.Reference))
                throw new PoisonMessageException("InvalidProviderResult");
            current.Status=result.Status; current.ProviderReference=result.Reference; current.NextReconcileAtUtc=null; current.Reason=result.Status=="Succeeded"?null:result.Status=="NotCharged"?"NotCharged":"SimulatedDecline";
            if(result.Status=="Succeeded") AddIntent(new PaymentSucceeded(current.Id,result.Reference!),current,"succeeded");
            else AddIntent(new PaymentDeclined(current.Id,current.Reason!),current,"declined");
        }
        current.LeaseOwner=null; current.LeaseUntilUtc=null; current.Version++;
        await db.SaveChangesAsync(ct); await commit.CommitAsync(ct); return true;
    }
    private void AddIntent<T>(T payload,PaymentOperation operation,string outcome)
    {
        var message=Guid.NewGuid();
        foreach(var destination in MessageRoutes.Destinations(typeof(T).Name))
        {
            var delivery=Guid.NewGuid(); var now=clock.GetUtcNow();
            db.Outbox.Add(new PaymentOutbox {DeliveryId=delivery,MessageId=message,EffectKey=$"payment/{operation.Id}/{outcome}",Destination=destination,
                Type=typeof(T).Name,OccurredAtUtc=now,EnvelopeJson=MessageRoutes.Serialize(payload,message,delivery,now,operation.SagaId,operation.CorrelationId,operation.SourceMessageId)});
        }
    }
}
