using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Api.Application;
using Catalog.Api.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Api.Application;
using Notifications.Api.Persistence;
using Payments.Api.Application;
using Payments.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Lab.Tests;

public sealed class PaymentFactAttribute : FactAttribute
{
    public PaymentFactAttribute(){if(Environment.GetEnvironmentVariable("LAB_PAYMENT_TEST")!="1")Skip="Use scripts/Verify-Payments.ps1 for controlled real PostgreSQL/ElasticMQ workflow and effect crash tests.";}
}
[Collection("Local lab")]
public sealed class PaymentWorkflowTests
{
    private sealed class SimulatedCrash : Exception;
    internal sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T:DbContext
    {public T CreateDbContext()=>create();}
    internal static PaymentsDb Payments()=>new(new DbContextOptionsBuilder<PaymentsDb>().UseNpgsql(ReservationTestSupport.Connection("payments")).Options);
    internal static NotificationsDb Notifications()=>new(new DbContextOptionsBuilder<NotificationsDb>().UseNpgsql(ReservationTestSupport.Connection("notifications")).Options);
    private static DurableFakeProvider Provider()=>new(new Factory<PaymentsDb>(Payments),TimeProvider.System);
    private static DurableFakeSender Sender()=>new(new Factory<NotificationsDb>(Notifications),TimeProvider.System);
    private static async Task<IntegrationEnvelope<JsonElement>> ReadReservationIntent(Guid saga,string effect)
    {
        await using var db=ReservationTestSupport.Reservations();return MessageCodec.Parse((await db.Outbox.SingleAsync(o=>o.EffectKey==$"saga/{saga}/{effect}")).EnvelopeJson);
    }
    private static async Task ConsumePayment(MessagingTests.Lab lab,ReceivedDelivery delivery)
    {await using var db=Payments();await new MessagePump(new PaymentConsumer(db),lab.Transport,Options.Create(lab.Options)).ProcessAsync(delivery,default);}
    private static async Task ConsumeNotification(MessagingTests.Lab lab,ReceivedDelivery delivery)
    {await using var db=Notifications();await new MessagePump(new NotificationConsumer(db,TimeProvider.System),lab.Transport,Options.Create(lab.Options)).ProcessAsync(delivery,default);}
    private static async Task<bool> ProcessPayment(Guid id,Func<CancellationToken,Task>? barrier=null)
    {await using var db=Payments();return await new PaymentProcessor(db,Provider(),TimeProvider.System).ProcessAsync(default,id,barrier);}
    private static async Task<bool> ProcessNotification(Guid id,Func<CancellationToken,Task>? barrier=null)
    {await using var db=Notifications();return await new NotificationProcessor(db,Sender(),TimeProvider.System).ProcessAsync(default,id,barrier);}
    private static async Task<MessagingTests.Creation> StartAsync(MessagingTests.Lab lab,string mode)
    {
        // Hosted workers are paused: deterministic per-operation mode snapshot, without racing a global live queue.
        await SelectFaultAsync("payments", mode);
        var creation=await MessagingTests.CreateAsync();
        await lab.DispatchAsync("reservations",creation.HoldDelivery);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        await DispatchCatalog(lab,creation.HoldId,"held");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        var payment=await ReadReservationIntent(creation.Accepted.SagaId,"payment");
        await lab.DispatchAsync("reservations",payment.DeliveryId);await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));
        return creation;
    }
    private static async Task DispatchCatalog(MessagingTests.Lab lab,Guid hold,string outcome)
    {await using var db=ReservationTestSupport.Catalog();await lab.DispatchAsync("catalog",(await db.Outbox.SingleAsync(o=>o.EffectKey==$"hold/{hold}/{outcome}")).DeliveryId);}
    private static async Task DispatchPayment(MessagingTests.Lab lab,Guid operation,string outcome)
    {await using var db=Payments();await lab.DispatchAsync("payments",(await db.Outbox.SingleAsync(o=>o.EffectKey==$"payment/{operation}/{outcome}")).DeliveryId);await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));}
    private static async Task<Guid> PaymentId(Guid saga)
    {await using var db=ReservationTestSupport.Reservations();return(await db.Sagas.SingleAsync(s=>s.Id==saga)).PaymentOperationId;}
    private static async Task FinishConfirmed(MessagingTests.Lab lab,MessagingTests.Creation creation)
    {
        var confirm=await ReadReservationIntent(creation.Accepted.SagaId,"confirm");await lab.DispatchAsync("reservations",confirm.DeliveryId);
        await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));await DispatchCatalog(lab,creation.HoldId,"confirmed");
        await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        var terminal=await ReadReservationIntent(creation.Accepted.SagaId,"confirmed");await lab.DispatchAsync("reservations",terminal.DeliveryId);
        await ConsumeNotification(lab,await lab.ReceiveAsync("tourlab-notifications"));
    }
    private static async Task ExpirePaymentLease(Guid id)
    {await using var db=Payments();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE payments.\"Operations\" SET \"LeaseUntilUtc\"=CURRENT_TIMESTAMP-INTERVAL '1 second',\"NextReconcileAtUtc\"=CURRENT_TIMESTAMP WHERE \"Id\"={id}");}
    private static async Task MatureNotification(Guid id)
    {await using var db=Notifications();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications.\"Notifications\" SET \"LeaseUntilUtc\"=CURRENT_TIMESTAMP-INTERVAL '1 second',\"NextAttemptAtUtc\"=CURRENT_TIMESTAMP WHERE \"Id\"={id}");}

    [PaymentFact]
    public async Task CompleteSuccessAndDifferentMessageDuplicatesHaveOneChargeInventoryAndSimulatedDelivery()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Success");var id=await PaymentId(creation.Accepted.SagaId);
        var payment=await ReadReservationIntent(creation.Accepted.SagaId,"payment");
        var duplicate=payment with {MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()};
        await lab.Transport.SendAsync("tourlab-payments",JsonSerializer.Serialize(duplicate,MessageCodec.Json),default);await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));
        // Same operation ID with contradictory authorized content must remain poison.
        var conflict=duplicate with {MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid(),Payload=JsonSerializer.SerializeToElement(MessageCodec.Payload<ProcessPayment>(payment) with {AmountMinor=12346},MessageCodec.Json)};
        await lab.Transport.SendAsync("tourlab-payments",JsonSerializer.Serialize(conflict,MessageCodec.Json),default);
        var conflictedDelivery = await lab.ReceiveAsync("tourlab-payments");
        await Assert.ThrowsAsync<PoisonMessageException>(()=>ConsumePayment(lab,conflictedDelivery));
        Assert.True(await ProcessPayment(id));await DispatchPayment(lab,id,"succeeded");
        // Actual provider-generated success duplicated with a different message ID.
        await using(var db=Payments())
        {
            var succeeded=MessageCodec.Parse((await db.Outbox.SingleAsync(o=>o.EffectKey==$"payment/{id}/succeeded")).EnvelopeJson);
            await lab.Transport.SendAsync("tourlab-reservations",JsonSerializer.Serialize(succeeded with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},MessageCodec.Json),default);
        }
        await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));await FinishConfirmed(lab,creation);
        var confirm=await ReadReservationIntent(creation.Accepted.SagaId,"confirm");
        await lab.Transport.SendAsync("tourlab-catalog",JsonSerializer.Serialize(confirm with {MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},MessageCodec.Json),default);
        await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog")); // actual duplicate ConfirmSeats; no second inventory effect
        await using(var catalog=ReservationTestSupport.Catalog())
        {
            var confirmed=MessageCodec.Parse((await catalog.Outbox.SingleAsync(o=>o.EffectKey==$"hold/{creation.HoldId}/confirmed")).EnvelopeJson);
            await lab.Transport.SendAsync("tourlab-reservations",JsonSerializer.Serialize(confirmed with {MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},MessageCodec.Json),default);
            Assert.Equal(0,(await catalog.Sessions.SingleAsync(s=>s.Id==creation.Session.Id)).AvailableSeats);
            Assert.Equal("Confirmed",(await catalog.Holds.SingleAsync(h=>h.Id==creation.HoldId)).Status);
        }
        await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        var notice=await ReadReservationIntent(creation.Accepted.SagaId,"confirmed");
        await lab.Transport.SendAsync("tourlab-notifications",JsonSerializer.Serialize(notice with {MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},MessageCodec.Json),default);await ConsumeNotification(lab,await lab.ReceiveAsync("tourlab-notifications"));
        Guid notification;
        await using(var db=Notifications()){var row=await db.Notifications.SingleAsync(n=>n.ReservationId==creation.Accepted.ReservationId);Assert.Equal("Pending",row.Status);notification=row.Id;}
        Assert.True(await ProcessNotification(notification));Assert.False(await ProcessNotification(notification));
        await using(var db=Payments()){Assert.Equal(1,await db.Effects.CountAsync(e=>e.Id==id && e.Status=="Succeeded"));Assert.Equal(1,await db.Operations.CountAsync(o=>o.Id==id));}
        await using(var db=Notifications()){Assert.Equal(1,await db.Receipts.CountAsync(r=>r.Id==notification && r.DeliveredAtUtc!=null));Assert.Equal("Sent",(await db.Notifications.SingleAsync(n=>n.Id==notification)).Status);}
        await using(var db=ReservationTestSupport.Reservations()){Assert.Equal("Confirmed",(await db.Reservations.SingleAsync(r=>r.Id==creation.Accepted.ReservationId)).Status);Assert.Equal(1,await db.History.CountAsync(h=>h.SagaId==creation.Accepted.SagaId && h.From!=h.To && h.To=="Confirmed"));}
        await CheckOwnership(creation.Accepted.ReservationId,id,notification);
    }
    [PaymentFact]
    public async Task DeclineReleasesBeforeFailedAndDoesNotCharge()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Decline");var id=await PaymentId(creation.Accepted.SagaId);
        Assert.True(await ProcessPayment(id));await DispatchPayment(lab,id,"declined");
        await using(var db=ReservationTestSupport.Reservations())Assert.Equal("Compensating",(await db.Sagas.SingleAsync(s=>s.Id==creation.Accepted.SagaId)).State);
        var release=await ReadReservationIntent(creation.Accepted.SagaId,"release");await lab.DispatchAsync("reservations",release.DeliveryId);
        await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));await DispatchCatalog(lab,creation.HoldId,"released");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        await using(var db=ReservationTestSupport.Reservations()){var saga=await db.Sagas.SingleAsync(s=>s.Id==creation.Accepted.SagaId);Assert.Equal("Failed",saga.State);Assert.True(saga.SeatsReleased);Assert.False(saga.RefundRequired);Assert.Equal("SimulatedDecline",saga.FailureReason);}
        await using(var db=Payments())Assert.False(await db.Effects.AnyAsync(e=>e.Id==id && e.Status=="Succeeded"));
        await using(var db=ReservationTestSupport.Catalog())Assert.Equal(1,(await db.Sessions.SingleAsync(s=>s.Id==creation.Session.Id)).AvailableSeats);
        var notice=await ReadReservationIntent(creation.Accepted.SagaId,"failed");await lab.DispatchAsync("reservations",notice.DeliveryId);await ConsumeNotification(lab,await lab.ReceiveAsync("tourlab-notifications"));
        await using var notifications=Notifications();var row=await notifications.Notifications.SingleAsync(n=>n.ReservationId==creation.Accepted.ReservationId);Assert.True(await ProcessNotification(row.Id));
    }
    [PaymentFact]
    public async Task ProviderCommitCrashConcurrentRecoveryAndTimeoutLookupNeverChargeAgain()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Success");var id=await PaymentId(creation.Accepted.SagaId);
        await Assert.ThrowsAsync<SimulatedCrash>(()=>ProcessPayment(id,_=>throw new SimulatedCrash()));
        await using(var db=Payments()){Assert.Equal("Pending",(await db.Operations.SingleAsync(o=>o.Id==id)).Status);Assert.Equal(1,await db.Effects.CountAsync(e=>e.Id==id));Assert.False(await db.Outbox.AnyAsync(o=>o.EffectKey==$"payment/{id}/succeeded"));}
        await ExpirePaymentLease(id);var recovered=await Task.WhenAll(ProcessPayment(id),ProcessPayment(id));Assert.Single(recovered,r=>r);
        await using(var db=Payments()){Assert.Equal("Succeeded",(await db.Operations.SingleAsync(o=>o.Id==id)).Status);Assert.Equal(1,await db.Effects.CountAsync(e=>e.Id==id));}
        var timed=await StartAsync(lab,"TimeoutAfterCharge");var timedId=await PaymentId(timed.Accepted.SagaId);Assert.True(await ProcessPayment(timedId));
        await using(var db=Payments()){Assert.Equal("Unknown",(await db.Operations.SingleAsync(o=>o.Id==timedId)).Status);Assert.Equal("Succeeded",(await db.Effects.SingleAsync(e=>e.Id==timedId)).Status);}
        var admin=await ReservationTestSupport.IdentityAsync("Admin");using var client=IntegrationTests.CreateClient();using var request=new HttpRequestMessage(HttpMethod.Post,$"https://localhost:8443/api/payments/v1/admin/payments/{timedId}/reconcile");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",admin.Token);
        using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        Assert.True(await ProcessPayment(timedId));
        await using(var db=Payments()){Assert.Equal("Succeeded",(await db.Operations.SingleAsync(o=>o.Id==timedId)).Status);Assert.Equal(1,await db.Effects.CountAsync(e=>e.Id==timedId));}
        await DispatchPayment(lab,timedId,"unknown");await DispatchPayment(lab,timedId,"succeeded");await FinishConfirmed(lab,timed);
    }
    [PaymentFact]
    public async Task SenderFailureAfterAcceptanceAndReceiptBeforeResultCrashRecoverOneDelivery()
    {
        var envelope=MessageCodec.Parse(MessageRoutes.Serialize(new ReservationConfirmed(Guid.NewGuid(),"sender-evidence","Simulated notification"),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()));
        await SelectFaultAsync("notifications", "FailureThenSuccess");
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();
        await lab.Transport.SendAsync("tourlab-notifications",JsonSerializer.Serialize(envelope,MessageCodec.Json),default);
        await ConsumeNotification(lab,await lab.ReceiveAsync("tourlab-notifications")); // accepted work committed and real broker ACKed before sender failure
        Guid id;await using(var db=Notifications())id=(await db.Notifications.SingleAsync(n=>n.SourceEventId==envelope.MessageId)).Id;
        Assert.True(await ProcessNotification(id));await MatureNotification(id);Assert.True(await ProcessNotification(id));await MatureNotification(id);
        await Assert.ThrowsAsync<SimulatedCrash>(()=>ProcessNotification(id,_=>throw new SimulatedCrash()));
        await using(var db=Notifications()){Assert.Equal("Pending",(await db.Notifications.SingleAsync(n=>n.Id==id)).Status);var receipt=await db.Receipts.SingleAsync(r=>r.Id==id);Assert.Equal(2,receipt.Failures);Assert.NotNull(receipt.DeliveredAtUtc);}
        await MatureNotification(id);var outcomes=await Task.WhenAll(ProcessNotification(id),ProcessNotification(id));Assert.Single(outcomes,x=>x);
        await using(var db=Notifications()){Assert.Equal("Sent",(await db.Notifications.SingleAsync(n=>n.Id==id)).Status);Assert.Equal(1,await db.Receipts.CountAsync(r=>r.Id==id && r.DeliveredAtUtc!=null));}
    }
    private sealed class FutureClock : TimeProvider {public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UtcNow.AddMinutes(11);}
    [PaymentFact]
    public async Task ChargedConfirmationRejectionPreservesUnfinishedRefundAndNeverPublishesTerminalFailure()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Success");var id=await PaymentId(creation.Accepted.SagaId);
        await ProcessPayment(id);await DispatchPayment(lab,id,"succeeded");var confirm=await ReadReservationIntent(creation.Accepted.SagaId,"confirm");
        await lab.DispatchAsync("reservations",confirm.DeliveryId);var delivery=await lab.ReceiveAsync("tourlab-catalog");
        await using(var db=ReservationTestSupport.Catalog())await new MessagePump(new CatalogConsumer(db,new InventoryHandlers(db,new FutureClock())),lab.Transport,Options.Create(lab.Options)).ProcessAsync(delivery,default);
        await DispatchCatalog(lab,creation.HoldId,"confirmation-rejected");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        var release=await ReadReservationIntent(creation.Accepted.SagaId,"release");await lab.DispatchAsync("reservations",release.DeliveryId);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        await DispatchCatalog(lab,creation.HoldId,"released");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        await using var reservations=ReservationTestSupport.Reservations();var saga=await reservations.Sagas.SingleAsync(s=>s.Id==creation.Accepted.SagaId);
        Assert.Equal("Compensating",saga.State);Assert.True(saga.RefundRequired);Assert.False(saga.RefundCompleted);Assert.True(saga.SeatsReleased);Assert.NotNull(saga.ProviderReference);
        Assert.True(await reservations.Outbox.AnyAsync(o=>o.EffectKey==$"saga/{saga.Id}/refund"));Assert.False(await reservations.Outbox.AnyAsync(o=>o.EffectKey==$"saga/{saga.Id}/failed"));
    }
    private static async Task CheckOwnership(Guid reservation,Guid payment,Guid notification)
    {
        var bob=await ReservationTestSupport.IdentityAsync("Bob");var admin=await ReservationTestSupport.IdentityAsync("Admin");
        using var client=IntegrationTests.CreateClient();
        using var reconcile = new HttpRequestMessage(HttpMethod.Post,$"https://localhost:8443/api/payments/v1/admin/payments/{payment}/reconcile");
        reconcile.Headers.Authorization=new("Bearer",bob.Token);
        using var denied = await client.SendAsync(reconcile);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        foreach(var service in new[]{"payments","notifications"})
        {
            var id=service=="payments"?payment:notification;
            using var request=new HttpRequestMessage(HttpMethod.Get,$"https://localhost:8443/api/{service}/v1/{service}/{id}");request.Headers.Authorization=new("Bearer",bob.Token);
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);
            using var list=new HttpRequestMessage(HttpMethod.Get,$"https://localhost:8443/api/{service}/v1/{service}?reservationId={reservation}");list.Headers.Authorization=new("Bearer",bob.Token);
            using var result=await client.SendAsync(list);Assert.Equal(HttpStatusCode.OK,result.StatusCode);
            var json=await result.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(0,service=="payments"?json.GetArrayLength():json.GetProperty("items").GetArrayLength());
            using var adminRead=new HttpRequestMessage(HttpMethod.Get,$"https://localhost:8443/api/{service}/v1/{service}/{id}");adminRead.Headers.Authorization=new("Bearer",admin.Token);
            using var adminResult=await client.SendAsync(adminRead);Assert.Equal(HttpStatusCode.OK,adminResult.StatusCode);
            using var fault=new HttpRequestMessage(HttpMethod.Put,$"https://localhost:8443/api/{service}/v1/admin/faults"){Content=JsonContent.Create(new FaultSelection(service=="payments"?"Success":"None",1))};fault.Headers.Authorization=new("Bearer",bob.Token);
            using var forbidden=await client.SendAsync(fault);Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
        }
    }
    private static async Task SelectFaultAsync(string service, string mode)
    {
        var admin = await ReservationTestSupport.IdentityAsync("Admin"); using var client = IntegrationTests.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put,$"https://localhost:8443/api/{service}/v1/admin/faults")
            { Content = JsonContent.Create(new FaultSelection(mode,1)) };
        request.Headers.Authorization = new("Bearer",admin.Token);
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(new FaultView(mode,1), await response.Content.ReadFromJsonAsync<FaultView>());
    }
}
