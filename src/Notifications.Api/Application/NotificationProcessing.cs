using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Notifications.Api.Application;

public interface INotificationSender { Task<DateTimeOffset> SendAsync(Notification notification,CancellationToken ct); }
public sealed class DurableFakeSender(IDbContextFactory<NotificationsDb> factory,TimeProvider clock) : INotificationSender
{
    public async Task<DateTimeOffset> SendAsync(Notification notification,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct); await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"sender/" + notification.Id},0))",ct);
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{notification.Id,notification.SourceEventId,notification.Kind,notification.UserId,notification.ReservationId,notification.Subject,notification.Body,notification.Mode})));
        var receipt=await db.Receipts.SingleOrDefaultAsync(r=>r.Id==notification.Id,ct);
        if(receipt is not null && receipt.RequestHash!=hash) throw new PoisonMessageException("SenderIdentityConflict");
        if(receipt is null) {receipt=new DeliveryReceipt {Id=notification.Id,SourceEventId=notification.SourceEventId,Kind=notification.Kind,RequestHash=hash}; db.Receipts.Add(receipt);}
        if(receipt.DeliveredAtUtc is not null) return receipt.DeliveredAtUtc.Value;
        var fail=notification.Mode=="FailureThenSuccess" && receipt.Failures<2;
        if(fail) receipt.Failures++; else receipt.DeliveredAtUtc=clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        if(fail) throw new SimulatedSenderFailure();
        return receipt.DeliveredAtUtc!.Value;
    }
}
public sealed class SimulatedSenderFailure : Exception;
public sealed class NotificationConsumer(NotificationsDb db,TimeProvider clock) : IInboxConsumer
{
    public async Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope,CancellationToken ct)
    {
        Guid reservation; string user; string body;
        if(envelope.Type=="ReservationConfirmed") {var p=MessageCodec.Payload<ReservationConfirmed>(envelope); reservation=p.ReservationId;user=p.UserId;body=p.Summary;}
        else if(envelope.Type=="ReservationCancelled") {var p=MessageCodec.Payload<ReservationCancelled>(envelope);reservation=p.ReservationId;user=p.UserId;body=p.Summary;}
        else if(envelope.Type=="ReservationFailed") {var p=MessageCodec.Payload<ReservationFailed>(envelope);reservation=p.ReservationId;user=p.UserId;body=p.Reason;}
        else throw new PoisonMessageException("NotificationHandlerNotImplemented");
        if(reservation==Guid.Empty || string.IsNullOrWhiteSpace(user) || user.Length>128 || string.IsNullOrWhiteSpace(body) || body.Length>1000) throw new PoisonMessageException("InvalidNotification");
        await InboxTransaction.ProcessAsync(db,"notifications","NotificationAcceptance",envelope,async token=>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"notification/"+reservation+"/"+envelope.Type},0))",token);
            var existing=await db.Notifications.SingleOrDefaultAsync(n=>n.ReservationId==reservation && n.Kind==envelope.Type,token);
            if(existing is not null)
            {
                if(existing.UserId!=user || existing.Body!=body || existing.SagaId!=envelope.SagaId) throw new PoisonMessageException("NotificationIdentityConflict");
                return;
            }
            var fault=(await db.Faults.FromSqlRaw("SELECT * FROM notifications.\"Faults\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(token)).Single();
            var mode=fault.Remaining>0?fault.Mode:"None"; if(fault.Remaining>0)fault.Remaining--;
            db.Notifications.Add(new Notification {Id=Guid.NewGuid(),ReservationId=reservation,UserId=user,SagaId=envelope.SagaId!.Value,
                SourceEventId=envelope.MessageId,Kind=envelope.Type,Subject="SIMULATED "+envelope.Type,Body=body,Mode=mode,CreatedAtUtc=clock.GetUtcNow(),NextAttemptAtUtc=clock.GetUtcNow()});
        },ct);
    }
}
public sealed class NotificationProcessor(NotificationsDb db,INotificationSender sender,TimeProvider clock)
{
    public async Task<bool> ProcessAsync(CancellationToken ct,Guid? selected=null,Func<CancellationToken,Task>? afterSender=null)
    {
        db.ChangeTracker.Clear(); Notification? operation;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            operation=(await db.Notifications.FromSqlInterpolated($"""
                SELECT * FROM notifications."Notifications" WHERE "Status"='Pending' AND "NextAttemptAtUtc"<=CURRENT_TIMESTAMP
                AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc"<=CURRENT_TIMESTAMP)
                AND ({selected}::uuid IS NULL OR "Id"={selected})
                ORDER BY "CreatedAtUtc","Id" FOR UPDATE SKIP LOCKED LIMIT 1
                """).ToListAsync(ct)).SingleOrDefault();
            if(operation is null)return false;
            operation.LeaseOwner=Guid.NewGuid();operation.LeaseUntilUtc=clock.GetUtcNow().AddSeconds(60);operation.Version++;
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        var owner=operation.LeaseOwner;DateTimeOffset? delivered=null;
        try{delivered=await sender.SendAsync(operation,ct);}catch(SimulatedSenderFailure){}
        if(afterSender is not null)await afterSender(ct);
        db.ChangeTracker.Clear();await using var commit=await db.Database.BeginTransactionAsync(ct);
        var current=(await db.Notifications.FromSqlInterpolated($"SELECT * FROM notifications.\"Notifications\" WHERE \"Id\"={operation.Id} FOR UPDATE").ToListAsync(ct)).Single();
        if(current.LeaseOwner!=owner || current.LeaseUntilUtc<=clock.GetUtcNow())return false;
        if(delivered is not null){current.Status="Sent";current.SentAtUtc=delivered;}
        else current.NextAttemptAtUtc=clock.GetUtcNow().AddSeconds(5);
        current.LeaseOwner=null;current.LeaseUntilUtc=null;current.Version++;
        await db.SaveChangesAsync(ct);await commit.CommitAsync(ct);return true;
    }
}
