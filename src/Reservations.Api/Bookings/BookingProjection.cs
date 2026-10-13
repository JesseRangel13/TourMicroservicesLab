using System.Security.Claims;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservations.Api.Application;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;

namespace Reservations.Api.Bookings;

// External projection intentionally has no PostgreSQL Inbox gate: conditional DynamoDB writes deduplicate effects.
public sealed class BookingProjectionConsumer(IBookingStore store) : IInboxConsumer
{
    public Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope, CancellationToken ct) =>
        store.ApplyAsync(MessageCodec.Payload<BookingSnapshotChanged>(envelope), ct);
}
public sealed class BookingRebuilder(ReservationsDb db)
{
    public async Task<RebuildBookingsResult> RebuildAsync(RebuildBookings request, CancellationToken ct)
    {
        if (request.BatchSize is < 1 or > 100) throw new ReservationProblem(400, "InvalidRebuildBatch");
        // PostgreSQL uuid keyset ordering; no mutable offset or cross-schema join.
        var rows = request.AfterId is { } after
            ? await db.Reservations.FromSqlInterpolated($"SELECT * FROM reservations.\"Reservations\" WHERE \"Id\">{after} ORDER BY \"Id\" LIMIT {request.BatchSize}").AsNoTracking().ToArrayAsync(ct)
            : await db.Reservations.AsNoTracking().OrderBy(x => x.Id).Take(request.BatchSize).ToArrayAsync(ct);
        foreach (var row in rows) {
            var snapshot = BookingSnapshots.From(row); var message = Guid.NewGuid(); var delivery = Guid.NewGuid();
            db.Outbox.Add(new ReservationOutbox { DeliveryId = delivery, MessageId = message, EffectKey = $"booking-rebuild/{delivery}",
                Destination = "tourlab-booking-projection", Type = nameof(BookingSnapshotChanged), OccurredAtUtc = snapshot.UpdatedAtUtc,
                EnvelopeJson = MessageRoutes.Serialize(snapshot, message, delivery, snapshot.UpdatedAtUtc, null, row.Id, row.Id) });
        }
        await db.SaveChangesAsync(ct);
        return new(rows.Length, rows.Length == request.BatchSize ? rows[^1].Id : null);
    }
}
public sealed class BookingProjectionWorker(IServiceScopeFactory scopes, IOptions<BookingProjectionOptions> options,
    IOptions<MessagingOptions> messaging, ILogger<BookingProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled || !messaging.Value.Enabled) return;
        while (!stop.IsCancellationRequested) {
            try {
                await using var scope = scopes.CreateAsyncScope();
                var transport = scope.ServiceProvider.GetRequiredService<IMessageTransport>();
                var deliveries = await transport.ReceiveAsync("tourlab-booking-projection", 20, 60, stop);
                foreach (var delivery in deliveries) {
                    var consumer = scope.ServiceProvider.GetRequiredService<BookingProjectionConsumer>();
                    await new MessagePump(consumer, transport, messaging, scope.ServiceProvider.GetRequiredService<ILogger<MessagePump>>()).ProcessAsync(delivery, stop);
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Booking projection failed; delivery remains retryable"); await Task.Delay(TimeSpan.FromSeconds(2), stop); }
        }
    }
}
public sealed class BookingOutboxWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    IOptions<BookingProjectionOptions> options, IOptions<MessagingOptions> messaging, ILogger<BookingOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled || !messaging.Value.Enabled) return;
        while (!stop.IsCancellationRequested) {
            try {
                await using var scope = scopes.CreateAsyncScope();
                var store = new PgOutboxStore(configuration.GetConnectionString("Runtime") ?? throw new InvalidOperationException("Runtime connection required"),
                    "reservations", onlyDestination: "tourlab-booking-projection");
                var sent = await new OutboxDispatcher(store, scope.ServiceProvider.GetRequiredService<IMessageTransport>()).DispatchAsync(60, stop);
                if (sent == 0) await Task.Delay(TimeSpan.FromSeconds(2), stop);
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Projection publication failed; business Outbox drain remains independent"); await Task.Delay(TimeSpan.FromSeconds(2), stop); }
        }
    }
}
public static class BookingEndpoints
{
    public static void AddBookingProjection(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<BookingProjectionOptions>().Bind(config.GetSection("BookingProjection"))
            .Validate(o => !o.Enabled || o.Table.StartsWith("tourlab-", StringComparison.Ordinal) && ValidKey(o.CursorKey), "Owned booking table and dedicated 32byte cursor key required").ValidateOnStart();
        services.AddSingleton(sp => DynamoBookingStore.Create(sp.GetRequiredService<IOptions<BookingProjectionOptions>>().Value));
        services.AddSingleton<BookingCursor>(); services.AddSingleton<IBookingStore, DynamoBookingStore>();
        services.AddScoped<BookingProjectionConsumer>(); services.AddScoped<BookingRebuilder>(); services.AddHostedService<BookingProjectionWorker>(); services.AddHostedService<BookingOutboxWorker>();
    }
    private static bool ValidKey(string value) { try { return Convert.FromBase64String(value).Length == 32; } catch (FormatException) { return false; } }
    public static void MapBookings(this WebApplication app)
    {
        var api = app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) => {
            try { return await next(ctx); }
            catch (ReservationProblem e) { return Results.Problem(statusCode: e.Status, title: e.Code, extensions: new Dictionary<string, object?> { ["code"] = e.Code }); }
            catch (AmazonDynamoDBException) { return Results.Problem(statusCode: 503, title: "BookingDashboardUnavailable"); }
            catch (HttpRequestException) { return Results.Problem(statusCode: 503, title: "BookingDashboardUnavailable"); }
            catch (TimeoutException) { return Results.Problem(statusCode: 503, title: "BookingDashboardUnavailable"); }
            catch (OperationCanceledException) when (!ctx.HttpContext.RequestAborted.IsCancellationRequested) { return Results.Problem(statusCode: 503, title: "BookingDashboardUnavailable"); }
        });
        api.MapGet("/my-bookings", (ClaimsPrincipal user, int? pageSize, string? cursor, IBookingStore store, CancellationToken ct) =>
            store.ListAsync(Owner(user), pageSize ?? 10, cursor, ct)).RequireAuthorization("Api");
        api.MapGet("/my-bookings/{id:guid}", async (ClaimsPrincipal user, Guid id, IBookingStore store, CancellationToken ct) => {
            var item = await store.GetAsync(Owner(user), id, ct); return item is null ? Results.NotFound() : Results.Ok(item);
        }).RequireAuthorization("Api");
        api.MapPost("/admin/my-bookings/rebuild", (RebuildBookings request, BookingRebuilder rebuilder, CancellationToken ct) =>
            rebuilder.RebuildAsync(request, ct)).RequireAuthorization("AdminApi");
    }
    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? throw new ReservationProblem(401, "InvalidIdentity");
}
