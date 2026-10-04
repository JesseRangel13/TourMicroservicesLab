using Catalog.Api.Application;
using Catalog.Api.Persistence;
using Lab.Provisioner;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
namespace Lab.Tests;

[Collection("Local lab")]
public sealed class CatalogTests
{
    private static CatalogDb Open() => new(new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(LocalConfiguration.Connection("localhost",
        "catalog_runtime", IntegrationTests.Settings.RuntimePasswords["catalog"], Path.Combine(IntegrationTests.Root, ".local", "certificates"))).Options);
    private static async Task<T> Run<T>(Func<CatalogDb, Task<T>> action) { await using var db = Open(); return await action(db); }
    private sealed class LabClock(DateTimeOffset now) : TimeProvider
    {
        private long ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
    private sealed record Fixture(TourView Tour, SessionView Session, LabClock Clock);
    private static async Task<Fixture> Setup(int capacity = 1, long amount = 12345)
    {
        var clock = new LabClock(DateTimeOffset.UtcNow);
        var tour = await Run(db => new CatalogCommands(db, clock).CreateAsync(new("Evidence " + Guid.NewGuid(), "Preserved transactional test data."), default));
        var session = await Run(db => new CatalogCommands(db, clock).CreateSessionAsync(tour.Id, new(clock.GetUtcNow().AddDays(2), amount, capacity), default));
        return new(tour, session, clock);
    }
    private static CreateQuote Request(Fixture f, int participants = 1) => new(Guid.NewGuid(), f.Session.Id, participants, "catalog-test-user", f.Session.UnitAmountMinor, "MXN");
    private static Task<QuoteView> Quote(Fixture f, CreateQuote? request = null) => Run(db => new QuoteHandler(db, f.Clock).HandleAsync(request ?? Request(f), default));
    private static CatalogMessageContext Context() { var saga = Guid.NewGuid(); return new(Guid.NewGuid(), saga, saga); }
    private static HoldSeats Hold(QuoteView quote) => new(Guid.NewGuid(), quote.QuoteId, Guid.NewGuid(), "catalog-test-user");
    private static Task<InventoryResult> Hold(Fixture f, HoldSeats command, CatalogMessageContext context) => Run(db => new InventoryHandlers(db, f.Clock).HoldAsync(command, context, default));
    private static Task<InventoryResult> Release(Fixture f, HoldSeats command, CatalogMessageContext context) => Run(db => new InventoryHandlers(db, f.Clock).ReleaseAsync(new(command.HoldId, "Test release"), context, default));
    private static Task<SessionView> Session(Fixture f) => Run(async db => CatalogRules.View(await db.Sessions.SingleAsync(s => s.Id == f.Session.Id)));

    [LocalFact]
    public async Task ConcurrentFinalSeatHasExactlyOneWinner()
    {
        var f = await Setup(); var q1 = await Quote(f); var q2 = await Quote(f);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<InventoryResult> Compete(QuoteView q) { await gate.Task; return await Hold(f, Hold(q), Context()); }
        var first = Compete(q1); var second = Compete(q2); gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.Succeeded); Assert.Single(results, r => r.Reason == "InsufficientSeats");
        Assert.Equal(0, (await Session(f)).AvailableSeats);
    }
    [LocalFact]
    public async Task DuplicateHoldAndReleaseHaveOneInventoryEffectAndOneIntentPerTransition()
    {
        var f = await Setup(4); var quote = await Quote(f, Request(f, 2)); var command = Hold(quote); var context = Context();
        var results = await Task.WhenAll(Hold(f, command, context), Hold(f, command, context));
        Assert.All(results, r => Assert.True(r.Succeeded)); Assert.Equal(2, (await Session(f)).AvailableSeats);
        await Task.WhenAll(Release(f, command, context), Release(f, command, context));
        Assert.Equal(4, (await Session(f)).AvailableSeats);
        await Run(async db => { Assert.Equal(2, await db.Outbox.CountAsync(o => o.EffectKey.StartsWith($"hold/{command.HoldId}/"))); return true; });
    }
    [LocalFact]
    public async Task ConfirmationAndExpirationSerializeAtTheDeadline()
    {
        var f = await Setup(); var command = Hold(await Quote(f)); var context = Context(); await Hold(f, command, context);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        var confirm = Run(db => new InventoryHandlers(db, f.Clock).ConfirmAsync(new(command.HoldId), context, default));
        var expire = Run(db => new InventoryHandlers(db, f.Clock).ExpireAsync(command.HoldId, default));
        var result = await confirm; await expire;
        Assert.False(result.Succeeded); Assert.Equal(1, (await Session(f)).AvailableSeats);
        await Release(f, command, context); await Run(db => new InventoryHandlers(db, f.Clock).ExpireAsync(command.HoldId, default));
        Assert.Equal(1, (await Session(f)).AvailableSeats);
        await Run(async db => { Assert.Equal(1, await db.Outbox.CountAsync(o => o.EffectKey == $"hold/{command.HoldId}/expired")); Assert.Equal(1, await db.Outbox.CountAsync(o => o.EffectKey == $"hold/{command.HoldId}/released")); return true; });
    }
    [LocalFact]
    public async Task ConfirmationBeforeDeadlineWinsAndCanBeReleasedOnce()
    {
        var f = await Setup(); var command = Hold(await Quote(f)); var context = Context(); await Hold(f, command, context);
        var confirm = Run(db => new InventoryHandlers(db, f.Clock).ConfirmAsync(new(command.HoldId), context, default));
        var expire = Run(db => new InventoryHandlers(db, f.Clock).ExpireAsync(command.HoldId, default));
        Assert.True((await confirm).Succeeded); await expire;
        f.Clock.Advance(TimeSpan.FromMinutes(11)); await Run(db => new InventoryHandlers(db, f.Clock).ExpireAsync(command.HoldId, default));
        Assert.Equal(0, (await Session(f)).AvailableSeats);
        Assert.True((await Run(db => new InventoryHandlers(db, f.Clock).ConfirmAsync(new(command.HoldId), context, default))).Succeeded);
        await Release(f, command, context); await Release(f, command, context); Assert.Equal(1, (await Session(f)).AvailableSeats);
    }
    [LocalFact]
    public async Task ReleaseBeforeHoldPersistsTombstoneAndPreventsLateDeduction()
    {
        var f = await Setup(); var command = Hold(await Quote(f)); var context = Context();
        await Release(f, command, context);
        Assert.False((await Hold(f, command, context)).Succeeded); Assert.Equal(1, (await Session(f)).AvailableSeats);
        await Run(async db => { var row = await db.Holds.SingleAsync(h => h.Id == command.HoldId); Assert.Equal("Released", row.Status); Assert.Null(row.SessionId); return true; });
    }
    [LocalFact]
    public async Task ValidQuoteKeepsItsPriceAfterPriceChangesAndCreatesTransactionalProjectionIntent()
    {
        var f = await Setup(); var request = Request(f); var quote = await Quote(f, request);
        var changed = await Run(db => new CatalogCommands(db, f.Clock).PriceAsync(f.Session.Id, new(99999, f.Session.Version), default));
        var replay = await Quote(f, request); Assert.Equal(quote.QuoteId, replay.QuoteId); Assert.Equal(12345, replay.UnitAmountMinor);
        Assert.Equal(99999, replay.Session.UnitAmountMinor); Assert.True((await Hold(f, Hold(quote), Context())).Succeeded);
        await Run(async db => {
            var row = await db.Outbox.SingleAsync(o => o.EffectKey == $"session/{f.Session.Id}/{changed.PriceVersion}");
            Assert.Contains("TourSessionChanged", row.EnvelopeJson); Assert.Contains("99999", row.EnvelopeJson); return true;
        });
    }
    [LocalFact]
    public async Task QuoteReplayMatchesOriginalPayloadAndRejectsChangedOrExpiredRequests()
    {
        var f = await Setup(); var request = Request(f);
        var pair = await Task.WhenAll(Quote(f, request), Quote(f, request)); Assert.Equal(pair[0].QuoteId, pair[1].QuoteId);
        var mismatch = await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f, request with { Participants = 2 })); Assert.Equal("RequestIdConflict", mismatch.Code);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f, request)); Assert.Equal("QuoteExpired", expired.Code);
        Assert.Equal("QuoteExpired", (await Hold(f, Hold(pair[0]), Context())).Reason); Assert.Equal(1, (await Session(f)).AvailableSeats);
    }
    [LocalFact]
    public async Task UnacceptedAmountOrCurrencyCannotCreateQuote()
    {
        var f = await Setup();
        foreach (var request in new[] { Request(f) with { ExpectedUnitAmountMinor = 1 }, Request(f) with { ExpectedCurrency = "USD" } })
            Assert.Equal("PriceChanged", (await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f, request))).Code);
    }
    [LocalFact]
    public async Task CapacityCannotDropBelowHeldOrConfirmedSeatsAndVersionsPreventLostUpdates()
    {
        var f = await Setup(3); var command = Hold(await Quote(f, Request(f, 2))); var context = Context(); await Hold(f, command, context);
        var current = await Session(f);
        Assert.Equal("CapacityOccupied", (await Assert.ThrowsAsync<CatalogProblem>(() => Run(db => new CatalogCommands(db, f.Clock).CapacityAsync(current.Id, new(1, current.Version), default)))).Code);
        Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogProblem>(() => Run(db => new CatalogCommands(db, f.Clock).CapacityAsync(current.Id, new(3, f.Session.Version), default)))).Code);
        await Run(db => new InventoryHandlers(db, f.Clock).ConfirmAsync(new(command.HoldId), context, default));
        Assert.Equal("CapacityOccupied", (await Assert.ThrowsAsync<CatalogProblem>(() => Run(db => new CatalogCommands(db, f.Clock).CapacityAsync(current.Id, new(1, current.Version), default)))).Code);
        var updated = await Run(db => new CatalogCommands(db, f.Clock).CapacityAsync(current.Id, new(4, current.Version), default)); Assert.Equal(2, updated.AvailableSeats);
    }
    [LocalFact]
    public async Task InactiveTourCannotQuoteOrHoldAndProjectionChangesWithActivation()
    {
        var f = await Setup(); var quote = await Quote(f);
        await Run(db => new CatalogCommands(db, f.Clock).UpdateAsync(f.Tour.Id, new(f.Tour.Name, f.Tour.Description, false, f.Tour.Version), default));
        Assert.Equal("SessionUnavailable", (await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f))).Code);
        Assert.Equal("SessionUnavailable", (await Hold(f, Hold(quote), Context())).Reason); Assert.Equal(1, (await Session(f)).AvailableSeats);
    }
    [LocalFact]
    public async Task ParticipantLimitsFutureDepartureAndCheckedMoneyAreEnforced()
    {
        var f = await Setup(10, long.MaxValue);
        Assert.Equal("AmountOverflow", (await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f, Request(f, 2)))).Code);
        foreach (var participants in new[] { 0, 7 }) Assert.Equal(400, (await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f, Request(f, participants)))).Status);
        await Assert.ThrowsAsync<CatalogProblem>(() => Run(db => new CatalogCommands(db, f.Clock).CreateSessionAsync(f.Tour.Id, new(f.Clock.GetUtcNow(), 1, 1), default)));
        f.Clock.Advance(TimeSpan.FromDays(3)); Assert.Equal("SessionUnavailable", (await Assert.ThrowsAsync<CatalogProblem>(() => Quote(f))).Code);
    }
    [LocalFact]
    public async Task ContradictoryHoldIdentityAndQuoteReuseCannotChangeInventory()
    {
        var f = await Setup(3); var quote = await Quote(f); var command = Hold(quote); var context = Context(); await Hold(f, command, context);
        Assert.Equal("OperationIdentityConflict", (await Assert.ThrowsAsync<CatalogProblem>(() => Hold(f, command with { ReservationId = Guid.NewGuid() }, context))).Code);
        Assert.Equal("QuoteAlreadyUsed", (await Hold(f, Hold(quote), Context())).Reason); Assert.Equal(2, (await Session(f)).AvailableSeats);
        await Run(db => new InventoryHandlers(db, f.Clock).ReleaseAsync(new(command.HoldId, "Cleanup intention"), context, default));
    }
    [LocalFact]
    public async Task RejectedHoldDoesNotBecomeSuccessfulAfterCapacityIncreases()
    {
        var f = await Setup(0); var command = Hold(await Quote(f)); var context = Context();
        Assert.False((await Hold(f, command, context)).Succeeded);
        await Run(db => new CatalogCommands(db, f.Clock).CapacityAsync(f.Session.Id, new(3, f.Session.Version), default));
        Assert.False((await Hold(f, command, context)).Succeeded); Assert.Equal(3, (await Session(f)).AvailableSeats);
    }
    [LocalFact]
    public async Task BootstrapIsIdempotentAndCatalogRuntimeCannotReadMigrationHistory()
    {
        var before = await Run(db => db.Sessions.Where(s => s.Id == Guid.Parse("20000000-0000-0000-0000-000000000001")).SingleAsync());
        await Run(async db => { await CatalogSeed.RunAsync(db, default); return true; });
        var after = await Run(db => db.Sessions.Where(s => s.Id == before.Id).SingleAsync()); Assert.Equal(before.StartsAtUtc, after.StartsAtUtc);
        await using var db = Open(); var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM catalog.\"__EFMigrationsHistory\""));
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
