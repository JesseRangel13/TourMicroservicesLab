using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Api.Application;
using Catalog.Api.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Reservations.Api.Messaging;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Lab.Tests;

public sealed class MessagingFactAttribute : FactAttribute
{
    public MessagingFactAttribute() { if (Environment.GetEnvironmentVariable("LAB_MESSAGING_TEST") != "1") Skip = "Use scripts/Verify-Messaging.ps1 for controlled workers/crash windows with real PostgreSQL and ElasticMQ."; }
}
[Collection("Local lab")]
public sealed class MessagingTests
{
    private sealed class SimulatedCrash : Exception;
    internal sealed class Lab : IAsyncDisposable
    {
        internal AmazonSQSClient Sqs { get; } = new(new BasicAWSCredentials("local-simulation", "local-simulation"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:9324", AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(25), MaxErrorRetry = 0 });
        internal MessagingOptions Options { get; } = new() { Enabled = true, Local = true, Endpoint = "http://localhost:9324", InputQueue = "tourlab-catalog" };
        internal SqsTransport Transport => new(Sqs, Microsoft.Extensions.Options.Options.Create(Options));
        internal async Task InitAsync()
        {
            var prefix = "tourlab-evidence-" + Guid.NewGuid().ToString("N");
            foreach (var service in new[] { "catalog", "reservations", "payments", "notifications" })
            { var queue = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = prefix + "-" + service }); Options.QueueUrls.Add("tourlab-" + service, queue.QueueUrl); }
        }
        internal async Task<ReceivedDelivery> ReceiveAsync(string queue)
        {
            for (var i = 0; i < 40; i++) { var rows = await Transport.ReceiveAsync(queue, 0, 60, default); if (rows.Length != 0) return rows[0]; await Task.Delay(50); }
            throw new InvalidOperationException("Expected a real broker delivery.");
        }
        internal async Task DispatchAsync(string schema, Guid id, Func<OutboxDelivery, CancellationToken, Task>? barrier = null, int lease = 60)
        { var count = await new OutboxDispatcher(new PgOutboxStore(ReservationTestSupport.Connection(schema), schema, id), Transport).DispatchAsync(lease, default, barrier); Assert.Equal(1, count); }
        internal async Task CatalogAsync(ReceivedDelivery delivery, Func<CancellationToken, Task>? barrier = null)
        {
            await using var db = ReservationTestSupport.Catalog(); var consumer = new CatalogConsumer(db, new InventoryHandlers(db, TimeProvider.System));
            await new MessagePump(consumer, Transport, Microsoft.Extensions.Options.Options.Create(Options)).ProcessAsync(delivery, default, barrier);
        }
        internal async Task ReservationsAsync(ReceivedDelivery delivery)
        {
            await using var db = ReservationTestSupport.Reservations(); var consumer = new ReservationConsumer(db, TimeProvider.System);
            await new MessagePump(consumer, Transport, Microsoft.Extensions.Options.Options.Create(Options)).ProcessAsync(delivery, default);
        }
        public ValueTask DisposeAsync() { Sqs.Dispose(); return ValueTask.CompletedTask; } // evidence queues/messages are retained
    }
    internal sealed record Creation(ReservationAccepted Accepted, SessionView Session, Guid HoldDelivery, Guid HoldId);
    internal static async Task<Creation> CreateAsync()
    {
        var session = await ReservationTestSupport.SessionAsync(1); var alice = await ReservationTestSupport.IdentityAsync("Alice");
        using var client = IntegrationTests.CreateClient(); using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost:8445/v1/reservations")
        { Content = JsonContent.Create(new CreateReservation(session.Id, 1, session.UnitAmountMinor, "MXN")) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.Token); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<ReservationAccepted>())!;
        await using var db = ReservationTestSupport.Reservations(); var saga = await db.Sagas.SingleAsync(s => s.Id == accepted.SagaId);
        var outbox = await db.Outbox.SingleAsync(o => o.EffectKey == $"saga/{saga.Id}/hold");
        Assert.Null(outbox.PublishedAtUtc); Assert.Equal(1, await db.Idempotency.CountAsync(i => i.ResourceId == accepted.ReservationId));
        Assert.Equal(1, await db.History.CountAsync(h => h.SagaId == saga.Id));
        return new(accepted, session, outbox.DeliveryId, saga.HoldId);
    }
    private static async Task<Guid> HeldDeliveryAsync(Creation creation)
    { await using var db = ReservationTestSupport.Catalog(); return (await db.Outbox.SingleAsync(o => o.EffectKey == $"hold/{creation.HoldId}/held")).DeliveryId; }

    [MessagingFact]
    public async Task RecoveryAfterReservationCommitDispatchesDurableIntentAndLeavesPaymentCommandQueued()
    {
        var creation = await CreateAsync(); await using var lab = new Lab(); await lab.InitAsync();
        // A fresh dispatcher discovers committed work after the creator's HTTP request/process scope ended.
        await lab.DispatchAsync("reservations", creation.HoldDelivery);
        await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        await lab.DispatchAsync("catalog", await HeldDeliveryAsync(creation));
        await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        Guid paymentId;
        await using (var db = ReservationTestSupport.Reservations())
        {
            var saga = await db.Sagas.SingleAsync(s => s.Id == creation.Accepted.SagaId); Assert.Equal("AwaitingPayment", saga.State);
            Assert.Equal(2, await db.History.CountAsync(h => h.SagaId == saga.Id));
            var payment = await db.Outbox.SingleAsync(o => o.EffectKey == $"saga/{saga.Id}/payment"); paymentId = payment.DeliveryId;
            Assert.Equal(saga.PaymentOperationId, MessageCodec.Payload<ProcessPayment>(MessageCodec.Parse(payment.EnvelopeJson)).PaymentOperationId);
        }
        await lab.DispatchAsync("reservations", paymentId); var delivery = await lab.ReceiveAsync("tourlab-payments");
        Assert.Equal("ProcessPayment", MessageCodec.Parse(delivery.Body).Type);
        // No incomplete Payments stub consumes it: reset visibility only in this owned evidence queue.
        await lab.Transport.ExtendAsync(delivery, 0, default);
        var again = await lab.ReceiveAsync("tourlab-payments"); Assert.Equal(delivery.Body, again.Body);
    }
    [MessagingFact]
    public async Task SendBeforeMarkCrashRecoversExpiredLeaseAndDuplicatesDoNotDeductTwice()
    {
        var creation = await CreateAsync(); await using var lab = new Lab(); await lab.InitAsync();
        await Assert.ThrowsAsync<SimulatedCrash>(() => lab.DispatchAsync("reservations", creation.HoldDelivery, (_, _) => throw new SimulatedCrash(), 1));
        var first = await lab.ReceiveAsync("tourlab-catalog"); await lab.CatalogAsync(first);
        await Task.Delay(1200); await lab.DispatchAsync("reservations", creation.HoldDelivery);
        var repeated = await lab.ReceiveAsync("tourlab-catalog");
        Assert.Equal(MessageCodec.Parse(first.Body).MessageId, MessageCodec.Parse(repeated.Body).MessageId);
        Assert.Equal(MessageCodec.Parse(first.Body).DeliveryId, MessageCodec.Parse(repeated.Body).DeliveryId);
        await lab.CatalogAsync(repeated);
        await using var db = ReservationTestSupport.Catalog(); Assert.Equal(0, (await db.Sessions.SingleAsync(s => s.Id == creation.Session.Id)).AvailableSeats);
        Assert.Equal(1, await db.Set<Catalog.Api.Persistence.CatalogInbox>().CountAsync(i => i.MessageId == MessageCodec.Parse(first.Body).MessageId));
        Assert.Equal(1, await db.Outbox.CountAsync(o => o.EffectKey == $"hold/{creation.HoldId}/held"));
    }
    [MessagingFact]
    public async Task ConsumerCommitBeforeAckCrashRedeliversWithoutRepeatingInventoryOrSagaTransition()
    {
        var creation = await CreateAsync(); await using var lab = new Lab(); await lab.InitAsync(); await lab.DispatchAsync("reservations", creation.HoldDelivery);
        var first = await lab.ReceiveAsync("tourlab-catalog");
        await Assert.ThrowsAsync<SimulatedCrash>(() => lab.CatalogAsync(first, _ => throw new SimulatedCrash()));
        await lab.Transport.ExtendAsync(first, 0, default); await lab.CatalogAsync(await lab.ReceiveAsync("tourlab-catalog"));
        await lab.DispatchAsync("catalog", await HeldDeliveryAsync(creation)); var outcome = await lab.ReceiveAsync("tourlab-reservations");
        await using (var db = ReservationTestSupport.Reservations())
            await Assert.ThrowsAsync<SimulatedCrash>(() => new MessagePump(new ReservationConsumer(db, TimeProvider.System), lab.Transport, Options.Create(lab.Options))
                .ProcessAsync(outcome, default, _ => throw new SimulatedCrash()));
        await lab.Transport.ExtendAsync(outcome, 0, default); await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        await using var reservations = ReservationTestSupport.Reservations(); Assert.Equal(2, await reservations.History.CountAsync(h => h.SagaId == creation.Accepted.SagaId));
        Assert.Equal(1, await reservations.Outbox.CountAsync(o => o.EffectKey == $"saga/{creation.Accepted.SagaId}/payment"));
    }
    [MessagingFact]
    public async Task FailedEffectsRollBackInboxInventoryAndOutboxTogether()
    {
        var creation = await CreateAsync(); await using var reservations = ReservationTestSupport.Reservations();
        var envelope = MessageCodec.Parse((await reservations.Outbox.SingleAsync(o => o.DeliveryId == creation.HoldDelivery)).EnvelopeJson);
        await using (var db = ReservationTestSupport.Catalog())
        {
            await Assert.ThrowsAsync<SimulatedCrash>(() => InboxTransaction.ProcessAsync(db, "catalog", "CatalogInventory", envelope, async ct =>
            {
                await new InventoryHandlers(db, TimeProvider.System).HoldAsync(MessageCodec.Payload<HoldSeats>(envelope), new(envelope.MessageId, envelope.SagaId!.Value, envelope.CorrelationId), ct);
                throw new SimulatedCrash();
            }, default));
        }
        await using var check = ReservationTestSupport.Catalog(); Assert.Equal(1, (await check.Sessions.SingleAsync(s => s.Id == creation.Session.Id)).AvailableSeats);
        Assert.False(await check.Holds.AnyAsync(h => h.Id == creation.HoldId)); Assert.False(await check.Set<Catalog.Api.Persistence.CatalogInbox>().AnyAsync(i => i.MessageId == envelope.MessageId));
        Assert.False(await check.Outbox.AnyAsync(o => o.EffectKey == $"hold/{creation.HoldId}/held"));
    }
    [MessagingFact]
    public async Task CompetingPublishersClaimOneLeaseAndExpiredOwnerCannotMarkAnotherLease()
    {
        var creation = await CreateAsync(); var store = new PgOutboxStore(ReservationTestSupport.Connection("reservations"), "reservations", creation.HoldDelivery);
        var rows = await Task.WhenAll(store.ClaimAsync(1, 60, default), store.ClaimAsync(1, 60, default)); Assert.Equal(1, rows.Sum(r => r.Length)); var old = rows.SelectMany(r => r).Single();
        // Deterministically expire only this test's lease; connection startup must not decide the race.
        await using (var db = ReservationTestSupport.Reservations())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reservations.\"Outbox\" SET \"LeaseUntilUtc\" = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE \"DeliveryId\" = {old.DeliveryId} AND \"LeaseOwner\" = {old.LeaseOwner}");
        var next = Assert.Single(await store.ClaimAsync(1, 60, default));
        Assert.NotEqual(old.LeaseOwner, next.LeaseOwner); Assert.False(await store.MarkPublishedAsync(old, default));
        // This test does not mark unsent work; the new owner actually sends before publication is recorded.
        await using var lab = new Lab(); await lab.InitAsync(); await lab.Transport.SendAsync(next.Destination, next.Body, default); Assert.True(await store.MarkPublishedAsync(next, default));
    }
    [MessagingFact]
    public async Task UnsupportedPoisonMessageIsNotAckedAndBrokerMovesItToDlqAfterFiveReceives()
    {
        await using var lab = new Lab(); await lab.InitAsync(); var dlq = await lab.Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "tourlab-evidence-dlq-" + Guid.NewGuid().ToString("N") });
        var arn = (await lab.Sqs.GetQueueAttributesAsync(dlq.QueueUrl, ["QueueArn"])).Attributes["QueueArn"];
        await lab.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = lab.Options.QueueUrls["tourlab-reservations"], Attributes = new()
            { ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = arn, maxReceiveCount = 5 }) } });
        var body = MessageRoutes.Serialize(new SeatsConfirmed(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await lab.Transport.SendAsync("tourlab-reservations", body, default);
        for (var i = 0; i < 5; i++) { var received = await lab.ReceiveAsync("tourlab-reservations"); await Assert.ThrowsAsync<PoisonMessageException>(() => lab.ReservationsAsync(received)); await lab.Transport.ExtendAsync(received, 0, default); }
        _ = await lab.Transport.ReceiveAsync("tourlab-reservations", 0, 60, default);
        var dead = await lab.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlq.QueueUrl, WaitTimeSeconds = 1 });
        Assert.Equal(body, Assert.Single(dead.Messages).Body);
    }
    [MessagingFact]
    public async Task CatalogProjectionIntentDispatchesAndOlderVersionCannotOverwriteLatest()
    {
        var session = await ReservationTestSupport.SessionAsync(); Guid id;
        await using (var db = ReservationTestSupport.Catalog()) id = (await db.Outbox.SingleAsync(o => o.EffectKey == $"session/{session.Id}/1")).DeliveryId;
        await using var lab = new Lab(); await lab.InitAsync(); await lab.DispatchAsync("catalog", id); await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        foreach (var version in new[] { 3L, 2L })
        {
            var body = MessageRoutes.Serialize(new TourSessionChanged(session.Id, "Projection version " + version, true, version, version * 10000, "MXN"), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, null, Guid.NewGuid(), Guid.NewGuid());
            await lab.Transport.SendAsync("tourlab-reservations", body, default); await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        }
        await using var reservations = ReservationTestSupport.Reservations(); var projection = await reservations.Projections.SingleAsync(p => p.SessionId == session.Id); Assert.Equal(3, projection.PriceVersion); Assert.Equal(30000, projection.UnitAmountMinor);
    }
    [MessagingFact]
    public async Task RealCatalogFailureReturns503AndCreatesNoDurableReservationWork()
    {
        var session = await ReservationTestSupport.SessionAsync(); var alice = await ReservationTestSupport.IdentityAsync("Alice"); var key = Guid.NewGuid().ToString();
        await DockerAsync("stop", "catalog");
        try
        {
            // The caller budget must exceed the ten-second quote budget plus its own TLS handshake.
            using var client = IntegrationTests.CreateClient();client.Timeout=TimeSpan.FromSeconds(20); using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost:8445/v1/reservations") { Content = JsonContent.Create(new CreateReservation(session.Id, 1, session.UnitAmountMinor, "MXN")) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.Token); request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await using var db = ReservationTestSupport.Reservations(); Assert.False(await db.Idempotency.AnyAsync(i => i.UserId == alice.Id && i.Key == key));
            Assert.False(await db.Reservations.AnyAsync(r => r.SessionId == session.Id)); Assert.False(await db.Sagas.AnyAsync(s => db.Reservations.Any(r => r.Id == s.ReservationId && r.SessionId == session.Id)));
        }
        finally { await DockerAsync("start", "--wait", "catalog"); }
    }
    private static async Task DockerAsync(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("docker") { WorkingDirectory = IntegrationTests.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("compose"); foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Docker required.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); Assert.Equal(0, process.ExitCode);
    }
}
