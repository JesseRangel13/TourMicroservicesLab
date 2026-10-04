using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Api.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Application;
using Reservations.Api.Application;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
using static Lab.Tests.PaymentWorkflowTests;
namespace Lab.Tests;

[Collection("Local lab")]
public sealed class SagaWorkflowTests
{
    internal sealed class Clock(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now {get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private sealed class Crash:Exception;
    private sealed class UncertainRefundProvider(IPaymentProvider inner):IPaymentProvider
    {
        public Task<ProviderCharge> ChargeAsync(Payments.Api.Persistence.PaymentOperation row,CancellationToken ct)=>inner.ChargeAsync(row,ct);
        public Task<ProviderCharge?> GetChargeStatusAsync(Guid id,CancellationToken ct)=>inner.GetChargeStatusAsync(id,ct);
        public Task RefundAsync(RefundPayment request,CancellationToken ct)=>throw new TimeoutException("Controlled uncertain refund");
        public Task<string?> GetRefundStatusAsync(Guid id,CancellationToken ct)=>inner.GetRefundStatusAsync(id,ct);
    }
    private sealed class TimeoutAfterRefundProvider(IPaymentProvider inner):IPaymentProvider
    {
        public Task<ProviderCharge> ChargeAsync(Payments.Api.Persistence.PaymentOperation row,CancellationToken ct)=>inner.ChargeAsync(row,ct);
        public Task<ProviderCharge?> GetChargeStatusAsync(Guid id,CancellationToken ct)=>inner.GetChargeStatusAsync(id,ct);
        public async Task RefundAsync(RefundPayment request,CancellationToken ct){await inner.RefundAsync(request,ct);throw new TimeoutException("Controlled timeout after durable refund");}
        public Task<string?> GetRefundStatusAsync(Guid id,CancellationToken ct)=>inner.GetRefundStatusAsync(id,ct);
    }
    private static DurableFakeProvider Provider(TimeProvider clock)=>new(new Factory<Payments.Api.Persistence.PaymentsDb>(PaymentWorkflowTests.Payments),clock);
    internal static async Task<ReservationSaga> Saga(Guid id)
    {await using var db=ReservationTestSupport.Reservations();return await db.Sagas.AsNoTracking().SingleAsync(x=>x.Id==id);}
    private static async Task<HttpResponseMessage> Request(string service,string path,string user="Admin",string? key=null,object? body=null)
    {
        var identity=await ReservationTestSupport.IdentityAsync(user);using var client=IntegrationTests.CreateClient();
        using var request=new HttpRequestMessage(HttpMethod.Post,$"https://localhost:8443/api/{service}/v1/{path}");request.Headers.Authorization=new("Bearer",identity.Token);
        if(key is not null)request.Headers.Add("Idempotency-Key",key);if(body is not null)request.Content=JsonContent.Create(body);
        return await client.SendAsync(request);
    }
    internal static async Task RefundAccept(MessagingTests.Lab lab,Guid saga,string effect="refund")
    {var intent=await ReadReservationIntent(saga,effect);await lab.DispatchAsync("reservations",intent.DeliveryId);await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));}
    internal static async Task<bool> RefundProcess(Guid refund,TimeProvider clock,Func<CancellationToken,Task>? barrier=null)
    {await using var db=PaymentWorkflowTests.Payments();return await new RefundProcessor(db,Provider(clock),clock).ProcessAsync(default,refund,barrier);}
    internal static async Task RefundResult(MessagingTests.Lab lab,Guid refund,string outcome="refunded")
    {
        await using var db=PaymentWorkflowTests.Payments();var intent=await db.Outbox.SingleAsync(x=>x.EffectKey==$"refund/{refund}/{outcome}");
        await lab.DispatchAsync("payments",intent.DeliveryId);await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
    }
    internal static async Task Release(MessagingTests.Lab lab,MessagingTests.Creation creation)
    {
        var intent=await ReadReservationIntent(creation.Accepted.SagaId,"release");await lab.DispatchAsync("reservations",intent.DeliveryId);
        await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));await DispatchCatalog(lab,creation.HoldId,"released");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
    }
    private static async Task Due(Guid saga,TimeProvider clock)
    {await using var db=ReservationTestSupport.Reservations();Assert.True(await new SagaRecovery(db,clock,new()).DueAsync(default,saga));}
    private static async Task<MessagingTests.Creation> Confirmed(MessagingTests.Lab lab,string mode="Success")
    {
        var creation=await StartAsync(lab,mode);var saga=await Saga(creation.Accepted.SagaId);await ProcessPayment(saga.PaymentOperationId);
        await DispatchPayment(lab,saga.PaymentOperationId,"succeeded");await FinishConfirmed(lab,creation);return creation;
    }
    private static async Task Capacity(MessagingTests.Creation creation)
    {await using var db=ReservationTestSupport.Catalog();Assert.Equal(1,(await db.Sessions.SingleAsync(x=>x.Id==creation.Session.Id)).AvailableSeats);}

    internal static async Task<MessagingTests.Creation> RestartCompensation(MessagingTests.Lab lab)
    {
        await SelectFaultAsync("catalog","RejectNextConfirmation");var creation=await StartAsync(lab,"Success");var saga=await Saga(creation.Accepted.SagaId);
        await ProcessPayment(saga.PaymentOperationId);await DispatchPayment(lab,saga.PaymentOperationId,"succeeded");
        var confirm=await ReadReservationIntent(saga.Id,"confirm");await lab.DispatchAsync("reservations",confirm.DeliveryId);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        await DispatchCatalog(lab,saga.HoldId,"confirmation-rejected");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        await RefundAccept(lab,saga.Id);await Release(lab,creation);
        await Assert.ThrowsAsync<Crash>(()=>RefundProcess(saga.RefundOperationId,TimeProvider.System,_=>throw new Crash()));
        Assert.Equal("Compensating",(await Saga(saga.Id)).State);return creation;
    }

    [PaymentFact]
    public async Task UncertainRefundBoundedAttemptsManualReviewAndAdminRetryKeepOriginalIds()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await Confirmed(lab);var saga=await Saga(creation.Accepted.SagaId);
        using(var cancel=await Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Alice",Guid.NewGuid().ToString()))Assert.Equal(HttpStatusCode.Accepted,cancel.StatusCode);
        await RefundAccept(lab,saga.Id);await Release(lab,creation);var clock=new Clock(DateTimeOffset.UtcNow);
        for(var i=0;i<5;i++)
        {
            await using var db=PaymentWorkflowTests.Payments();Assert.True(await new RefundProcessor(db,new UncertainRefundProvider(Provider(clock)),clock).ProcessAsync(default,saga.RefundOperationId));clock.Now=clock.Now.AddSeconds(31);
        }
        await RefundResult(lab,saga.RefundOperationId,"review");Assert.Equal("ManualReview",(await Saga(saga.Id)).State);
        var original=await ReadReservationIntent(saga.Id,"refund");
        await lab.Transport.SendAsync("tourlab-payments",JsonSerializer.Serialize(original with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},MessageCodec.Json),default);
        await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));
        await using(var db=PaymentWorkflowTests.Payments()){var unchanged=await db.Refunds.SingleAsync(x=>x.Id==saga.RefundOperationId);Assert.Equal("ManualReview",unchanged.Status);Assert.Equal(5,unchanged.Attempts);}
        using(var response=await Request("reservations",$"admin/sagas/{saga.Id}/retry-compensation"))Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var resumed=await Saga(saga.Id);Assert.Equal("CancellationPending",resumed.State);Assert.True(resumed.SeatsReleased);Assert.Equal(saga.RefundOperationId,resumed.RefundOperationId);
        await using(var db=ReservationTestSupport.Reservations())
        {
            Assert.False(await db.Outbox.AnyAsync(x=>x.EffectKey.StartsWith($"saga/{saga.Id}/release-retry/")));
            var retry=await db.Outbox.SingleAsync(x=>x.EffectKey.StartsWith($"saga/{saga.Id}/refund-retry/"));await lab.DispatchAsync("reservations",retry.DeliveryId);
        }
        await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));
        long retryVersion;await using(var db=PaymentWorkflowTests.Payments())retryVersion=(await db.Refunds.SingleAsync(x=>x.Id==saga.RefundOperationId)).LastRetryVersion;
        Assert.True(retryVersion>0);
        for(var i=0;i<5;i++)
        {
            await using var db=PaymentWorkflowTests.Payments();Assert.True(await new RefundProcessor(db,new UncertainRefundProvider(Provider(clock)),clock).ProcessAsync(default,saga.RefundOperationId));clock.Now=clock.Now.AddSeconds(31);
        }
        await RefundResult(lab,saga.RefundOperationId,$"review/{retryVersion}");Assert.Equal("ManualReview",(await Saga(saga.Id)).State);
        await using(var db=ReservationTestSupport.Reservations())
        {
            var stale=MessageCodec.Parse((await db.Outbox.SingleAsync(x=>x.EffectKey==$"saga/{saga.Id}/refund-retry/{retryVersion}")).EnvelopeJson);
            await lab.Transport.SendAsync("tourlab-payments",JsonSerializer.Serialize(stale with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},MessageCodec.Json),default);
        }
        await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));
        await using(var db=PaymentWorkflowTests.Payments()){var unchanged=await db.Refunds.SingleAsync(x=>x.Id==saga.RefundOperationId);Assert.Equal("ManualReview",unchanged.Status);Assert.Equal(5,unchanged.Attempts);}
        using(var response=await Request("reservations",$"admin/sagas/{saga.Id}/retry-compensation"))Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        await using(var db=ReservationTestSupport.Reservations())
        {
            var next=await db.Outbox.SingleAsync(x=>x.EffectKey.StartsWith($"saga/{saga.Id}/refund-retry/") && x.EffectKey!=$"saga/{saga.Id}/refund-retry/{retryVersion}");
            await lab.DispatchAsync("reservations",next.DeliveryId);
        }
        await ConsumePayment(lab,await lab.ReceiveAsync("tourlab-payments"));await RefundProcess(saga.RefundOperationId,TimeProvider.System);await RefundResult(lab,saga.RefundOperationId);
        Assert.Equal("Cancelled",(await Saga(saga.Id)).State);await Capacity(creation);
    }

    [PaymentFact]
    public async Task RealLateChargeAfterFailedReopensRepairWithoutDuplicateFailureNotice()
    {
        // Recovery-handler fixture: seed an earlier Failed decision locally; this is not full end-to-end evidence.
        // The later charge and success event come from the durable provider and real transport.
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Success");var saga=await Saga(creation.Accepted.SagaId);
        await using(var db=ReservationTestSupport.Reservations())
        {
            await using var tx=await db.Database.BeginTransactionAsync();var row=(await db.Sagas.FromSqlInterpolated($"SELECT * FROM reservations.\"Sagas\" WHERE \"Id\"={saga.Id} FOR UPDATE").ToListAsync()).Single();
            var reservation=await db.Reservations.SingleAsync(x=>x.Id==row.ReservationId);var recovery=new SagaRecovery(db,TimeProvider.System,new());
            var from=row.State;row.PaymentResolved=true;await recovery.CompensateAsync(row,reservation,"NotCharged",Guid.NewGuid(),default);recovery.Record(row,reservation,from,Guid.NewGuid());await db.SaveChangesAsync();await tx.CommitAsync();
        }
        await Release(lab,creation);Assert.Equal("Failed",(await Saga(saga.Id)).State);
        await ProcessPayment(saga.PaymentOperationId);await DispatchPayment(lab,saga.PaymentOperationId,"succeeded");Assert.Equal("Compensating",(await Saga(saga.Id)).State);
        await RefundAccept(lab,saga.Id);await RefundProcess(saga.RefundOperationId,TimeProvider.System);await RefundResult(lab,saga.RefundOperationId);
        Assert.Equal("Failed",(await Saga(saga.Id)).State);await Capacity(creation);
        await using var check=ReservationTestSupport.Reservations();Assert.Equal(1,await check.Outbox.CountAsync(x=>x.EffectKey==$"saga/{saga.Id}/failed"));Assert.False(await check.Outbox.AnyAsync(x=>x.EffectKey==$"saga/{saga.Id}/confirm"));
    }

    [PaymentFact]
    public async Task RefundTimeoutAfterProviderCommitRemainsPendingAndLookupRecoversOneEffect()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await Confirmed(lab);var saga=await Saga(creation.Accepted.SagaId);
        using(var response=await Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Alice",Guid.NewGuid().ToString()))Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        await RefundAccept(lab,saga.Id);var clock=new Clock(DateTimeOffset.UtcNow);
        await using(var db=PaymentWorkflowTests.Payments())Assert.True(await new RefundProcessor(db,new TimeoutAfterRefundProvider(Provider(clock)),clock).ProcessAsync(default,saga.RefundOperationId));
        await using(var db=PaymentWorkflowTests.Payments()){Assert.Equal("Pending",(await db.Refunds.SingleAsync(x=>x.Id==saga.RefundOperationId)).Status);Assert.NotNull((await db.ProviderRefunds.SingleAsync(x=>x.Id==saga.RefundOperationId)).Reference);}
        clock.Now=clock.Now.AddSeconds(31);await RefundProcess(saga.RefundOperationId,clock);await RefundResult(lab,saga.RefundOperationId);await Release(lab,creation);
        Assert.Equal("Cancelled",(await Saga(saga.Id)).State);await Capacity(creation);
        await using var check=PaymentWorkflowTests.Payments();Assert.Equal(1,await check.ProviderRefunds.CountAsync(x=>x.PaymentOperationId==saga.PaymentOperationId));
    }

    [PaymentFact]
    public async Task ConfirmationFaultRefundAndReleaseCompleteInEitherOrder()
    {
        foreach(var refundFirst in new[]{true,false})
        {
            await using var lab=new MessagingTests.Lab();await lab.InitAsync();await SelectFaultAsync("catalog","RejectNextConfirmation");
            var creation=await StartAsync(lab,"Success");var saga=await Saga(creation.Accepted.SagaId);
            await ProcessPayment(saga.PaymentOperationId);await DispatchPayment(lab,saga.PaymentOperationId,"succeeded");
            var confirm=await ReadReservationIntent(saga.Id,"confirm");await lab.DispatchAsync("reservations",confirm.DeliveryId);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
            await DispatchCatalog(lab,creation.HoldId,"confirmation-rejected");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
            Assert.Equal("Compensating",(await Saga(saga.Id)).State);await RefundAccept(lab,saga.Id);Assert.True(await RefundProcess(saga.RefundOperationId,TimeProvider.System));
            if(refundFirst)await RefundResult(lab,saga.RefundOperationId);else await Release(lab,creation);
            Assert.Equal("Compensating",(await Saga(saga.Id)).State);
            if(refundFirst)await Release(lab,creation);else await RefundResult(lab,saga.RefundOperationId);
            var complete=await Saga(saga.Id);Assert.Equal("Failed",complete.State);Assert.True(complete.RefundCompleted && complete.SeatsReleased);await Capacity(creation);
            await using var db=PaymentWorkflowTests.Payments();Assert.Equal(1,await db.ProviderRefunds.CountAsync(x=>x.PaymentOperationId==saga.PaymentOperationId));
        }
    }

    [PaymentFact]
    public async Task ConcurrentCancellationKeysOneRefundAndReleaseWithOwnershipAndDepartureChecks()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await Confirmed(lab);var saga=await Saga(creation.Accepted.SagaId);
        using(var denied=await Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Bob",Guid.NewGuid().ToString()))Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        using(var missing=await Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Alice"))Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);
        var keys=new[]{Guid.NewGuid().ToString(),Guid.NewGuid().ToString()};
        var responses=await Task.WhenAll(keys.Select(key=>Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Alice",key)));
        foreach(var response in responses){Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);response.Dispose();}
        Assert.Equal("CancellationPending",(await Saga(saga.Id)).State);
        await RefundAccept(lab,saga.Id);await Release(lab,creation);Assert.Equal("CancellationPending",(await Saga(saga.Id)).State);
        await RefundProcess(saga.RefundOperationId,TimeProvider.System);await RefundResult(lab,saga.RefundOperationId);
        Assert.Equal("Cancelled",(await Saga(saga.Id)).State);await Capacity(creation);
        using(var repeat=await Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Alice",Guid.NewGuid().ToString()))Assert.Equal(HttpStatusCode.Accepted,repeat.StatusCode);
        var intent=await ReadReservationIntent(saga.Id,"refund");await using(var db=PaymentWorkflowTests.Payments())
        {
            await new PaymentConsumer(db).ConsumeAsync(intent with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()},default);
            var conflict=intent with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid(),Payload=JsonSerializer.SerializeToElement(MessageCodec.Payload<RefundPayment>(intent) with{AmountMinor=1},MessageCodec.Json)};
            await Assert.ThrowsAsync<PoisonMessageException>(()=>new PaymentConsumer(db).ConsumeAsync(conflict,default));
        }
        await using(var db=PaymentWorkflowTests.Payments()){Assert.Equal(1,await db.Refunds.CountAsync(x=>x.PaymentOperationId==saga.PaymentOperationId));Assert.Equal(1,await db.ProviderRefunds.CountAsync(x=>x.PaymentOperationId==saga.PaymentOperationId));}
        var notice=await ReadReservationIntent(saga.Id,"cancelled");await lab.DispatchAsync("reservations",notice.DeliveryId);await ConsumeNotification(lab,await lab.ReceiveAsync("tourlab-notifications"));
        // A separate confirmed intention cannot be cancelled after departure (server clock).
        var departed=await Confirmed(lab);await using(var db=ReservationTestSupport.Reservations())
        {
            var clock=new Clock(DateTimeOffset.UtcNow.AddDays(366));var commands=new SagaCommands(db,new SagaRecovery(db,clock,new()),clock);
            var alice=await ReservationTestSupport.IdentityAsync("Alice");var error=await Assert.ThrowsAsync<ReservationProblem>(()=>commands.CancelAsync(departed.Accepted.ReservationId,alice.Id,false,"departure-evidence",default));Assert.Equal(409,error.Status);
        }
    }

    [PaymentFact]
    public async Task TwoTransientRefundFailuresAndProviderCommitCrashRecoverSameIdentifier()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await Confirmed(lab,"RefundTransientFailure");var saga=await Saga(creation.Accepted.SagaId);
        using(var response=await Request("reservations",$"reservations/{creation.Accepted.ReservationId}/cancel","Alice",Guid.NewGuid().ToString()))Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        await RefundAccept(lab,saga.Id);var clock=new Clock(DateTimeOffset.UtcNow);
        Assert.True(await RefundProcess(saga.RefundOperationId,clock));clock.Now=clock.Now.AddSeconds(31);
        Assert.True(await RefundProcess(saga.RefundOperationId,clock));clock.Now=clock.Now.AddSeconds(31);
        await Assert.ThrowsAsync<Crash>(()=>RefundProcess(saga.RefundOperationId,clock,_=>throw new Crash()));
        await using(var db=PaymentWorkflowTests.Payments()){Assert.Equal("Pending",(await db.Refunds.SingleAsync(x=>x.Id==saga.RefundOperationId)).Status);var effect=await db.ProviderRefunds.SingleAsync(x=>x.Id==saga.RefundOperationId);Assert.Equal(2,effect.Failures);Assert.NotNull(effect.Reference);}
        clock.Now=clock.Now.AddSeconds(61);var results=await Task.WhenAll(RefundProcess(saga.RefundOperationId,clock),RefundProcess(saga.RefundOperationId,clock));Assert.Single(results,x=>x);
        await RefundResult(lab,saga.RefundOperationId);Assert.Equal("CancellationPending",(await Saga(saga.Id)).State);await Release(lab,creation);
        Assert.Equal("Cancelled",(await Saga(saga.Id)).State);await Capacity(creation);
    }

    [PaymentFact]
    public async Task ExpirationThenActualLateChargeRefundsWithoutConfirmationOrDoubleCapacity()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Success");var saga=await Saga(creation.Accepted.SagaId);
        await using(var db=ReservationTestSupport.Catalog())await new InventoryHandlers(db,new Clock(DateTimeOffset.UtcNow.AddMinutes(11))).ExpireAsync(saga.HoldId,default);
        await DispatchCatalog(lab,saga.HoldId,"expired");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));Assert.Equal("PaymentUncertain",(await Saga(saga.Id)).State);
        await Release(lab,creation);await ProcessPayment(saga.PaymentOperationId);await DispatchPayment(lab,saga.PaymentOperationId,"succeeded");
        Assert.Equal("Compensating",(await Saga(saga.Id)).State);await RefundAccept(lab,saga.Id);await RefundProcess(saga.RefundOperationId,TimeProvider.System);await RefundResult(lab,saga.RefundOperationId);
        Assert.Equal("Failed",(await Saga(saga.Id)).State);await Capacity(creation);
        await using var reservations=ReservationTestSupport.Reservations();Assert.False(await reservations.Outbox.AnyAsync(x=>x.EffectKey==$"saga/{saga.Id}/confirm"));
    }

    [PaymentFact]
    public async Task AvailabilityDeadlineReleaseBeforeHoldAndLateHeldNeverStartPayment()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await MessagingTests.CreateAsync();var saga=await Saga(creation.Accepted.SagaId);
        await Due(saga.Id,new Clock(saga.DeadlineUtc.AddSeconds(1)));await Release(lab,creation);Assert.Equal("Failed",(await Saga(saga.Id)).State);
        await lab.DispatchAsync("reservations",creation.HoldDelivery);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        await DispatchCatalog(lab,saga.HoldId,"hold-rejected");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));await Capacity(creation);
        await using var db=ReservationTestSupport.Reservations();Assert.False(await db.Outbox.AnyAsync(x=>x.EffectKey==$"saga/{saga.Id}/payment"));
        // Separate actual held result delayed past abandonment is ACKed without creating payment.
        var delayed=await MessagingTests.CreateAsync();await lab.DispatchAsync("reservations",delayed.HoldDelivery);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        var late=await Saga(delayed.Accepted.SagaId);await Due(late.Id,new Clock(late.DeadlineUtc.AddSeconds(1)));
        await DispatchCatalog(lab,late.HoldId,"held");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));await Release(lab,delayed);await Capacity(delayed);
        await using var check=ReservationTestSupport.Reservations();Assert.False(await check.Outbox.AnyAsync(x=>x.EffectKey==$"saga/{late.Id}/payment"));
    }

    [PaymentFact]
    public async Task ConfirmationDeadlineConcurrentLateConfirmationKeepsCompensationAndOneFailureNotice()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"Success");var saga=await Saga(creation.Accepted.SagaId);
        await ProcessPayment(saga.PaymentOperationId);await DispatchPayment(lab,saga.PaymentOperationId,"succeeded");var confirm=await ReadReservationIntent(saga.Id,"confirm");
        await lab.DispatchAsync("reservations",confirm.DeliveryId);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog")); // real confirmation exists but result delayed
        var waiting=await Saga(saga.Id);var clock=new Clock(waiting.DeadlineUtc.AddSeconds(1));
        await Due(saga.Id,clock);await DispatchCatalog(lab,saga.HoldId,"confirmed");await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        Assert.Equal("Compensating",(await Saga(saga.Id)).State);await RefundAccept(lab,saga.Id);await RefundProcess(saga.RefundOperationId,TimeProvider.System);
        await using var payments=PaymentWorkflowTests.Payments();await using var catalog=ReservationTestSupport.Catalog();
        var refunded=MessageCodec.Parse((await payments.Outbox.SingleAsync(x=>x.EffectKey==$"refund/{saga.RefundOperationId}/refunded")).EnvelopeJson);
        var release=await ReadReservationIntent(saga.Id,"release");await lab.DispatchAsync("reservations",release.DeliveryId);await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        var released=MessageCodec.Parse((await catalog.Outbox.SingleAsync(x=>x.EffectKey==$"hold/{saga.HoldId}/released")).EnvelopeJson);
        async Task Apply(IntegrationEnvelope<JsonElement> message){await using var db=ReservationTestSupport.Reservations();await new Reservations.Api.Messaging.ReservationConsumer(db,TimeProvider.System).ConsumeAsync(message,default);}
        await Task.WhenAll(Apply(refunded),Apply(released));Assert.Equal("Failed",(await Saga(saga.Id)).State);await Capacity(creation);
        await Task.WhenAll(Apply(refunded with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()}),Apply(released with{MessageId=Guid.NewGuid(),DeliveryId=Guid.NewGuid()}));
        await using var check=ReservationTestSupport.Reservations();Assert.Equal(1,await check.Outbox.CountAsync(x=>x.EffectKey==$"saga/{saga.Id}/failed"));
    }

    [PaymentFact]
    public async Task UnknownFiveLookupsManualReviewAuthorizedProviderResolutionAndLateRepair()
    {
        foreach(var outcome in new[]{"Succeeded","NotCharged"})
        {
            await using var lab=new MessagingTests.Lab();await lab.InitAsync();var creation=await StartAsync(lab,"UnknownUntilAdminResolution");var saga=await Saga(creation.Accepted.SagaId);
            var clock=new Clock(DateTimeOffset.UtcNow);await using(var db=PaymentWorkflowTests.Payments())Assert.True(await new PaymentProcessor(db,Provider(clock),clock).ProcessAsync(default,saga.PaymentOperationId));
            await DispatchPayment(lab,saga.PaymentOperationId,"unknown");
            for(var i=0;i<5;i++){clock.Now=clock.Now.AddSeconds(31);await using var db=PaymentWorkflowTests.Payments();Assert.True(await new PaymentProcessor(db,Provider(clock),clock).ProcessAsync(default,saga.PaymentOperationId));}
            await using(var db=PaymentWorkflowTests.Payments()){Assert.Equal(5,(await db.Operations.SingleAsync(x=>x.Id==saga.PaymentOperationId)).ReconcileAttempts);Assert.False(await new PaymentProcessor(db,Provider(clock),clock).ProcessAsync(default,saga.PaymentOperationId));}
            await Due(saga.Id,new Clock(saga.DeadlineUtc.AddSeconds(1)));Assert.Equal("PaymentUncertain",(await Saga(saga.Id)).State);await Release(lab,creation);
            var uncertain=await Saga(saga.Id);await Due(saga.Id,new Clock(uncertain.DeadlineUtc.AddSeconds(1)));Assert.Equal("ManualReview",(await Saga(saga.Id)).State);
            using(var denied=await Request("payments",$"admin/fake-provider/{saga.PaymentOperationId}/resolve","Alice",body:new ProviderResolution(outcome)))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            using(var denied=await Request("reservations",$"admin/sagas/{saga.Id}/retry-compensation","Alice"))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            using(var response=await Request("payments",$"admin/fake-provider/{saga.PaymentOperationId}/resolve",body:new ProviderResolution(outcome)))Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
            Assert.Equal("ManualReview",(await Saga(saga.Id)).State); // provider resolution does not edit the Saga
            using(var response=await Request("payments",$"admin/payments/{saga.PaymentOperationId}/reconcile"))Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
            await ProcessPayment(saga.PaymentOperationId);await DispatchPayment(lab,saga.PaymentOperationId,outcome=="Succeeded"?"succeeded":"declined");
            if(outcome=="Succeeded"){Assert.Equal("Compensating",(await Saga(saga.Id)).State);await RefundAccept(lab,saga.Id);await RefundProcess(saga.RefundOperationId,TimeProvider.System);await RefundResult(lab,saga.RefundOperationId);}
            Assert.Equal("Failed",(await Saga(saga.Id)).State);await Capacity(creation);
        }
    }
}


