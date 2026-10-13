using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.Runtime;
using Lab.Provisioner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservations.Api.Application;
using Reservations.Api.Bookings;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;

namespace Lab.Tests;

[Collection("Local lab")]
public sealed class BookingExtensionTests
{
    private static BookingSnapshotChanged Snapshot(string user, Guid? id = null, long version = 1) =>
        new(user, id ?? Guid.NewGuid(), "Accepted tour name", DateTimeOffset.UtcNow.AddDays(3), 2, 24690, "MXN", "AwaitingAvailability", version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    [Fact]
    public void EncryptedCursorsRejectOtherUsersChangedQueriesTamperingAndExpiration()
    {
        var clock = new ImageExtensionTests.Clock();
        var o = Options.Create(new BookingProjectionOptions { CursorKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) });
        var cursor = new BookingCursor(o, clock);
        var key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = DynamoBookingStore.UserKey("Alice") }, ["sk"] = new() { S = "BOOKING#test" }, ["created"] = new() { S = "test" } };
        var token = cursor.Encode("Alice", 2, key); Assert.Equal(key["sk"].S, cursor.Decode("Alice", 2, token)?["sk"].S);
        Assert.Throws<ReservationProblem>(() => cursor.Decode("Bob", 2, token));
        Assert.Throws<ReservationProblem>(() => cursor.Decode("Alice", 3, token));
        Assert.Throws<ReservationProblem>(() => cursor.Decode("Alice", 2, "!" + token[1..]));
        clock.Advance(TimeSpan.FromMinutes(16)); Assert.Throws<ReservationProblem>(() => cursor.Decode("Alice", 2, token));
    }
    private static async Task<(DynamoBookingStore Store, IDisposable Client)> StoreAsync()
    {
        var options = new BookingProjectionOptions { Local = true, Endpoint = "http://localhost:8031", Table = "tourlab-test-" + Guid.NewGuid().ToString("N"), CursorKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        var client = DynamoBookingStore.Create(options); await client.CreateTableAsync(ExtensionBootstrap.Table(options.Table));
        var o = Options.Create(options); return (new(client, o, new BookingCursor(o, TimeProvider.System)), client);
    }
    [ExtensionFact]
    public async Task DynamoConditionalUpdatesPaginationAndForeignOwnerLookupsRemainSafe()
    {
        var (store, client) = await StoreAsync(); using var cleanup = client;
        var initial = Snapshot("Alice"); var newer = initial with { Version = 3, Status = "Confirmed", UpdatedAtUtc = DateTimeOffset.UtcNow };
        await store.ApplyAsync(newer, default); await store.ApplyAsync(initial, default); await store.ApplyAsync(newer, default);
        Assert.Equal(newer, await store.GetAsync("Alice", initial.ReservationId, default)); Assert.Null(await store.GetAsync("Bob", initial.ReservationId, default));
        await store.ApplyAsync(Snapshot("Alice"), default); await store.ApplyAsync(Snapshot("Alice"), default); await store.ApplyAsync(Snapshot("Bob"), default);
        var first = await store.ListAsync("Alice", 2, null, default); Assert.Equal(2, first.Items.Length); Assert.NotNull(first.Cursor);
        var next = await store.ListAsync("Alice", 2, first.Cursor, default); Assert.Single(next.Items); Assert.Null(next.Cursor);
        Assert.Empty(first.Items.Select(x => x.ReservationId).Intersect(next.Items.Select(x => x.ReservationId)));
        Assert.All(first.Items.Concat(next.Items), x => Assert.Equal("Alice", x.UserId));
        await Assert.ThrowsAsync<ReservationProblem>(() => store.ListAsync("Bob", 2, first.Cursor, default));
    }
    [ExtensionFact]
    public async Task FailureBeforePutAndCrashAfterPutBeforeAckRedeliverWithoutInboxSuppression()
    {
        var (store, client) = await StoreAsync(); using var cleanup = client;
        var fault = new FaultStore(store) { Fails = true }; var consumer = new BookingProjectionConsumer(fault);
        var transport = new Transport(); var message = Snapshot("Alice");
        var id = Guid.NewGuid(); var delivery = new ReceivedDelivery("tourlab-booking-projection", "receipt", MessageRoutes.Serialize(message, id, Guid.NewGuid(), DateTimeOffset.UtcNow, null, message.ReservationId, message.ReservationId));
        var pump = new MessagePump(consumer, transport, Options.Create(new MessagingOptions { VisibilitySeconds = 60 }));
        await Assert.ThrowsAsync<IOException>(() => pump.ProcessAsync(delivery, default)); Assert.Equal(0, transport.Acks); Assert.Null(await store.GetAsync(message.UserId, message.ReservationId, default));
        fault.Fails = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.ProcessAsync(delivery, default, _ => throw new InvalidOperationException("Injected after Dynamo success before ACK")));
        Assert.NotNull(await store.GetAsync(message.UserId, message.ReservationId, default)); Assert.Equal(0, transport.Acks);
        await pump.ProcessAsync(delivery, default); Assert.Equal(1, transport.Acks);
    }
    [ExtensionFact]
    public async Task ReservationTransactionEmitsSnapshotsAndDelayedRebuildCannotOverwriteNewerState()
    {
        var (store, client) = await StoreAsync(); using var cleanup = client;
        await using var db = ReservationTestSupport.Reservations(); var user = "extension-" + Guid.NewGuid().ToString("N");
        var session = await ReservationTestSupport.SessionAsync();
        var creator = new ReservationCreator(db, new QuoteClient(session), TimeProvider.System);
        var result = await creator.CreateAsync(user, Guid.NewGuid().ToString("N"), new(session.Id, 2, session.UnitAmountMinor, "MXN"), default);
        var original = await db.Outbox.SingleAsync(x => x.EffectKey == $"booking/{result.ResourceId}/v/1");
        var isolatedBusiness = new PgOutboxStore(ReservationTestSupport.Connection("reservations"), "reservations", original.DeliveryId, excludeDestination: "tourlab-booking-projection");
        Assert.Empty(await isolatedBusiness.ClaimAsync(1, 60, default));
        var isolatedProjection = new PgOutboxStore(ReservationTestSupport.Connection("reservations"), "reservations", original.DeliveryId, onlyDestination: "tourlab-booking-projection");
        Assert.Single(await isolatedProjection.ClaimAsync(1, 60, default));
        var sagaId = (await db.Sagas.SingleAsync(x => x.ReservationId == result.ResourceId)).Id;
        var hold = await db.Outbox.SingleAsync(x => x.EffectKey == $"saga/{sagaId}/hold");
        Assert.Single(await new PgOutboxStore(ReservationTestSupport.Connection("reservations"), "reservations", hold.DeliveryId, excludeDestination: "tourlab-booking-projection").ClaimAsync(1, 60, default));
        var snapshot = MessageCodec.Payload<BookingSnapshotChanged>(MessageCodec.Parse(original.EnvelopeJson)); Assert.Equal("Original accepted name", snapshot.TourName);
        var reservation = await db.Reservations.SingleAsync(x => x.Id == result.ResourceId);
        var predecessor = reservation.Id.ToByteArray(bigEndian: true);
        for (var i = predecessor.Length - 1; i >= 0; i--) { if (predecessor[i] > 0) { predecessor[i]--; break; } predecessor[i] = 255; }
        var rebuilt = await new BookingRebuilder(db).RebuildAsync(new(new Guid(predecessor, bigEndian: true), 1), default);
        Assert.Equal(1, rebuilt.Published);
        var delayedRebuild = db.ChangeTracker.Entries<Reservations.Api.Persistence.ReservationOutbox>().Single(x => x.Entity.EffectKey.StartsWith("booking-rebuild/", StringComparison.Ordinal)).Entity.EnvelopeJson;
        Assert.Equal(reservation.Id, MessageCodec.Payload<BookingSnapshotChanged>(MessageCodec.Parse(delayedRebuild)).ReservationId);
        reservation.Status = "Confirmed";
        await db.SaveChangesAsync(); Assert.Equal(2, reservation.Version);
        var latest = MessageCodec.Payload<BookingSnapshotChanged>(MessageCodec.Parse((await db.Outbox.SingleAsync(x => x.EffectKey == $"booking/{reservation.Id}/v/2")).EnvelopeJson));
        await store.ApplyAsync(latest, default); await store.ApplyAsync(snapshot, default);
        Assert.Equal("Confirmed", (await store.GetAsync(user, reservation.Id, default))?.Status);
        Assert.Equal("Original accepted name", (await store.GetAsync(user, reservation.Id, default))?.TourName);
        // The same conditional path handles stale rebuilds without resetting Inbox/table state.
        await new BookingProjectionConsumer(store).ConsumeAsync(MessageCodec.Parse(delayedRebuild), default);
        Assert.Equal(2, (await store.GetAsync(user, reservation.Id, default))?.Version);
    }
    [ExtensionFact]
    public async Task RealQueueRedeliveryAfterDynamoSuccessUsesConditionalVersionWithoutInboxGate()
    {
        var (store, client) = await StoreAsync(); using var cleanup = client;
        using var sqs = new AmazonSQSClient(new BasicAWSCredentials("local-simulation", "local-simulation"), new AmazonSQSConfig {
            ServiceURL = "http://localhost:9324", AuthenticationRegion = "us-east-1", MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(10) });
        var queue = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "tourlab-evidence-projection-" + Guid.NewGuid().ToString("N") });
        var settings = Options.Create(new MessagingOptions { Local = true, VisibilitySeconds = 60,
            QueueUrls = new() { ["tourlab-booking-projection"] = queue.QueueUrl } });
        var transport = new SqsTransport(sqs, settings); var snapshot = Snapshot("queue-" + Guid.NewGuid().ToString("N"));
        await transport.SendAsync("tourlab-booking-projection", MessageRoutes.Serialize(snapshot, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, null, snapshot.ReservationId, snapshot.ReservationId), default);
        var first = Assert.Single(await transport.ReceiveAsync("tourlab-booking-projection", 1, 60, default));
        var pump = new MessagePump(new BookingProjectionConsumer(store), transport, settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.ProcessAsync(first, default, _ => throw new InvalidOperationException("Injected crash before broker ACK")));
        await store.ApplyAsync(snapshot with { Version = 3, Status = "Confirmed" }, default);
        await transport.ExtendAsync(first, 0, default);
        var repeated = Assert.Single(await transport.ReceiveAsync("tourlab-booking-projection", 1, 60, default));
        Assert.Equal(MessageCodec.Parse(first.Body).MessageId, MessageCodec.Parse(repeated.Body).MessageId);
        await pump.ProcessAsync(repeated, default);
        Assert.Equal(3, (await store.GetAsync(snapshot.UserId, snapshot.ReservationId, default))?.Version);
        Assert.Empty(await transport.ReceiveAsync("tourlab-booking-projection", 0, 60, default));
    }
    [ExtensionFact]
    public async Task ApiDerivesOwnerAndReturns404ForAnotherUsersProjectedBooking()
    {
        var options = new BookingProjectionOptions { Local = true, Endpoint = "http://localhost:8031", Table = "tourlab-bookings", CursorKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        using var dynamo = DynamoBookingStore.Create(options); var o = Options.Create(options);
        var store = new DynamoBookingStore(dynamo, o, new BookingCursor(o, TimeProvider.System));
        var alice = await ReservationTestSupport.IdentityAsync("Alice"); var bob = await ReservationTestSupport.IdentityAsync("Bob"); var booking = Snapshot(alice.Id);
        await store.ApplyAsync(booking, default);
        using var client = IntegrationTests.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:8445/v1/my-bookings/{booking.ReservationId}"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bob.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        using var owned = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:8445/v1/my-bookings?pageSize=1&userId={alice.Id}"); owned.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bob.Token);
        var response = await client.SendAsync(owned); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All((await response.Content.ReadFromJsonAsync<BookingPage>())?.Items ?? [], x => Assert.Equal(bob.Id, x.UserId));
    }
    [ExtensionFact]
    public async Task DynamoOutageReturns503WhileAuthoritativeCreationStillCommits()
    {
        var session = await ReservationTestSupport.SessionAsync(); var alice = await ReservationTestSupport.IdentityAsync("Alice");
        using var client = IntegrationTests.CreateClient(); client.Timeout = TimeSpan.FromSeconds(30);
        await DockerAsync("stop", "dynamodb");
        try {
            using var dashboard = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8443/api/reservations/v1/my-bookings"); dashboard.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(dashboard)).StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost:8443/api/reservations/v1/reservations") { Content = JsonContent.Create(new CreateReservation(session.Id, 2, session.UnitAmountMinor, "MXN")) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.Token); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<ReservationAccepted>(); Assert.NotNull(accepted);
            await using var db = ReservationTestSupport.Reservations(); Assert.True(await db.Reservations.AnyAsync(x => x.Id == accepted.ReservationId));
            Assert.True(await db.Outbox.AnyAsync(x => x.EffectKey == $"booking/{accepted.ReservationId}/v/1"));
        } finally {
            await DockerAsync("up", "-d", "dynamodb");
            using var dynamo = DynamoBookingStore.Create(new BookingProjectionOptions { Local = true, Endpoint = "http://localhost:8031" });
            var ready = false;
            for (var i = 0; i < 12; i++) { try { await dynamo.DescribeTableAsync("tourlab-bookings"); ready = true; break; } catch { await Task.Delay(500); } }
            Assert.True(ready, "Owned DynamoDB emulator restored.");
        }
    }
    [ExtensionFact]
    public async Task CatalogRenameDoesNotRewriteQuotedOrAcceptedNameAndFailedWritesRollBackProjectionIntent()
    {
        var session = await ReservationTestSupport.SessionAsync(); var user = "extension-" + Guid.NewGuid().ToString("N"); var key = Guid.NewGuid().ToString("N");
        await using var catalog = ReservationTestSupport.Catalog(); var command = new CreateQuote(ReservationCreator.QuoteRequestId(user, key), session.Id, 2, user, session.UnitAmountMinor, "MXN");
        var quotes = new Catalog.Api.Application.QuoteHandler(catalog, TimeProvider.System); var original = await quotes.HandleAsync(command, default);
        var tour = await catalog.Tours.SingleAsync(x => x.Id == session.TourId); tour.Name = "Renamed after accepted quote"; tour.Version++; await catalog.SaveChangesAsync();
        var repeated = await quotes.HandleAsync(command, default); Assert.Equal(original.TourName, repeated.TourName);
        await using var db = ReservationTestSupport.Reservations(); var accepted = await new ReservationCreator(db, new FixedQuote(original), TimeProvider.System).CreateAsync(user, key, new(session.Id, 2, session.UnitAmountMinor, "MXN"), default);
        var row = await db.Reservations.SingleAsync(x => x.Id == accepted.ResourceId); Assert.Equal(original.TourName, row.TourName);
        var badId = Guid.NewGuid();
        db.Reservations.Add(new Reservations.Api.Persistence.Reservation { Id = badId, UserId = user, SessionId = session.Id, QuoteId = Guid.NewGuid(), Participants = 0,
            UnitAmountMinor = 100, TotalAmountMinor = 100, StartsAtUtc = session.StartsAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await using var check = ReservationTestSupport.Reservations(); Assert.False(await check.Reservations.AnyAsync(x => x.Id == badId));
        Assert.False(await check.Outbox.AnyAsync(x => x.EffectKey == $"booking/{badId}/v/1"));
    }
    private static async Task DockerAsync(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("compose"); foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker unavailable");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120)); await process.WaitForExitAsync(budget.Token); await Task.WhenAll(stdout, stderr);
        Assert.Equal(0, process.ExitCode);
    }
    private sealed class FixedQuote(QuoteView quote) : ICatalogQuoteClient
    {
        public Task<QuoteView> CreateAsync(CreateQuote request, CancellationToken ct) => Task.FromResult(quote);
    }
    private sealed class QuoteClient(SessionView session) : ICatalogQuoteClient
    {
        public Task<QuoteView> CreateAsync(CreateQuote request, CancellationToken ct) => Task.FromResult(new QuoteView(Guid.NewGuid(), session.UnitAmountMinor,
            session.UnitAmountMinor * request.Participants, "MXN", DateTimeOffset.UtcNow.AddMinutes(5), session, "Original accepted name"));
    }
    private sealed class FaultStore(IBookingStore actual) : IBookingStore
    {
        public bool Fails { get; set; }
        public Task ApplyAsync(BookingSnapshotChanged snapshot, CancellationToken ct) => Fails ? throw new IOException("Injected Dynamo outage") : actual.ApplyAsync(snapshot, ct);
        public Task<BookingSnapshotChanged?> GetAsync(string user, Guid id, CancellationToken ct) => actual.GetAsync(user, id, ct);
        public Task<BookingPage> ListAsync(string user, int size, string? cursor, CancellationToken ct) => actual.ListAsync(user, size, cursor, ct);
    }
    private sealed class Transport : IMessageTransport
    {
        public int Acks { get; private set; }
        public Task DeleteAsync(ReceivedDelivery delivery, CancellationToken ct) { Acks++; return Task.CompletedTask; }
        public Task ExtendAsync(ReceivedDelivery delivery, int seconds, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(string queue, string body, CancellationToken ct) => Task.CompletedTask;
        public Task<ReceivedDelivery[]> ReceiveAsync(string queue, int wait, int visibility, CancellationToken ct) => Task.FromResult(Array.Empty<ReceivedDelivery>());
    }
}
