using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Lab.Tests;

[Collection("Local lab")]
public sealed class ReservationHttpTests
{
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, "https://localhost:8443/api/reservations/v1/reservations" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
    [LocalFact]
    public async Task ConcurrentHttpCreationOwnershipReplayAndLiveConfirmedWorkflow()
    {
        var session = await ReservationTestSupport.SessionAsync(); var alice = await ReservationTestSupport.IdentityAsync("Alice");
        var bob = await ReservationTestSupport.IdentityAsync("Bob"); var admin = await ReservationTestSupport.IdentityAsync("Admin");
        using var client = IntegrationTests.CreateClient(); var key = Guid.NewGuid().ToString();
        var command = new CreateReservation(session.Id, 2, session.UnitAmountMinor, "MXN");
        var requests = await Task.WhenAll(Send(client, HttpMethod.Post, "", alice.Token, command, key), Send(client, HttpMethod.Post, "", alice.Token, command, key));
        try
        {
            Assert.All(requests, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
            var results = await Task.WhenAll(requests.Select(r => r.Content.ReadFromJsonAsync<ReservationAccepted>()));
            Assert.Equal(results[0], results[1]); var accepted = results[0]!;
            Assert.Equal($"/api/reservations/v1/reservations/{accepted.ReservationId}", requests[0].Headers.Location?.OriginalString);
            using var different = await Send(client, HttpMethod.Post, "", alice.Token, command with { Participants = 3 }, key); Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
            using var missingKey = await Send(client, HttpMethod.Post, "", alice.Token, command); Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
            using var otherDetail = await Send(client, HttpMethod.Get, $"/{accepted.ReservationId}", bob.Token); Assert.Equal(HttpStatusCode.NotFound, otherDetail.StatusCode);
            using var otherTimeline = await Send(client, HttpMethod.Get, $"/{accepted.ReservationId}/timeline", bob.Token); Assert.Equal(HttpStatusCode.NotFound, otherTimeline.StatusCode);
            using var adminDetail = await Send(client, HttpMethod.Get, $"/{accepted.ReservationId}", admin.Token); Assert.Equal(HttpStatusCode.OK, adminDetail.StatusCode);
            using var ownList = await Send(client, HttpMethod.Get, "", bob.Token); Assert.DoesNotContain((await ownList.Content.ReadFromJsonAsync<ReservationPage>())!.Items, r => r.Id == accepted.ReservationId);
            using var adminFilter = await Send(client, HttpMethod.Get, $"?userId={alice.Id}&status=AwaitingAvailability", admin.Token); Assert.Equal(HttpStatusCode.OK, adminFilter.StatusCode);
            using var cancel = await Send(client, HttpMethod.Post, $"/{accepted.ReservationId}/cancel", alice.Token, new { }, key); Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
            var state = "AwaitingAvailability";
            for (var attempt = 0; attempt < 90 && state != "Confirmed"; attempt++)
            {
                using var detail = await Send(client, HttpMethod.Get, $"/{accepted.ReservationId}", alice.Token);
                state = (await detail.Content.ReadFromJsonAsync<ReservationView>())!.Status;
                if (state != "Confirmed") await Task.Delay(500);
            }
            Assert.Equal("Confirmed", state);
            await using var db = ReservationTestSupport.Reservations();
            Assert.Equal(1, await db.Idempotency.CountAsync(i => i.UserId == alice.Id && i.Key == key));
            Assert.Equal(4, await db.History.CountAsync(h => h.SagaId == accepted.SagaId));
            var payment = await db.Outbox.AsNoTracking().SingleAsync(o => o.EffectKey == $"saga/{accepted.SagaId}/payment");
            for (var attempt = 0; attempt < 30 && payment.PublishedAtUtc is null; attempt++)
            { await Task.Delay(200); payment = await db.Outbox.AsNoTracking().SingleAsync(o => o.DeliveryId == payment.DeliveryId); }
            Assert.NotNull(payment.PublishedAtUtc);
            var payload = MessageCodec.Payload<ProcessPayment>(MessageCodec.Parse(payment.EnvelopeJson)); Assert.Equal(alice.Id, payload.UserId); Assert.Equal(24690, payload.AmountMinor);
            using var replay = await Send(client, HttpMethod.Post, "", alice.Token, command, key); Assert.Equal(accepted, await replay.Content.ReadFromJsonAsync<ReservationAccepted>());
            await using var catalog = ReservationTestSupport.Catalog(); Assert.Equal(8, (await catalog.Sessions.SingleAsync(s => s.Id == session.Id)).AvailableSeats);
            await using var payments = PaymentWorkflowTests.Payments();
            Assert.Equal(1, await payments.Effects.CountAsync(e => e.Id == payload.PaymentOperationId && e.Status == "Succeeded"));
            Shared.Contracts.NotificationView? delivered = null;
            for (var attempt = 0; attempt < 40 && delivered?.Status != "Sent"; attempt++)
            {
                await using var notifications = PaymentWorkflowTests.Notifications();
                var row = await notifications.Notifications.AsNoTracking().SingleOrDefaultAsync(n => n.ReservationId == accepted.ReservationId);
                if (row is not null) delivered = Notifications.Api.NotificationEndpoints.View(row);
                if (delivered?.Status != "Sent") await Task.Delay(250);
            }
            Assert.Equal("Sent", delivered?.Status);
            await using var receipts = PaymentWorkflowTests.Notifications();
            Assert.Equal(1, await receipts.Receipts.CountAsync(r => r.Id == delivered!.Id && r.DeliveredAtUtc != null));
        }
        finally { foreach (var response in requests) response.Dispose(); }
    }
    [LocalFact]
    public async Task TwoUsersCompeteThroughHttpAndSqsForTheFinalSeat()
    {
        var session = await ReservationTestSupport.SessionAsync(1); var alice = await ReservationTestSupport.IdentityAsync("Alice"); var bob = await ReservationTestSupport.IdentityAsync("Bob");
        using var client = IntegrationTests.CreateClient(); var command = new CreateReservation(session.Id, 1, session.UnitAmountMinor, "MXN");
        var responses = await Task.WhenAll(Send(client, HttpMethod.Post, "", alice.Token, command, Guid.NewGuid().ToString()),
            Send(client, HttpMethod.Post, "", bob.Token, command, Guid.NewGuid().ToString()));
        using var first = responses[0]; using var second = responses[1];
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode); Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var ids = new[] { (await first.Content.ReadFromJsonAsync<ReservationAccepted>())!.ReservationId, (await second.Content.ReadFromJsonAsync<ReservationAccepted>())!.ReservationId };
        string[] states = [];
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using var db = ReservationTestSupport.Reservations(); states = await db.Reservations.Where(r => ids.Contains(r.Id)).Select(r => r.Status).ToArrayAsync();
            if (states.All(s => s is "Confirmed" or "Failed")) break; await Task.Delay(500);
        }
        Assert.Single(states, s => s == "Confirmed"); Assert.Single(states, s => s == "Failed");
        await using var catalog = ReservationTestSupport.Catalog(); Assert.Equal(0, (await catalog.Sessions.SingleAsync(s => s.Id == session.Id)).AvailableSeats);
        await using var reservations = ReservationTestSupport.Reservations(); var rejectedSaga = await reservations.Sagas.SingleAsync(s => ids.Contains(s.ReservationId) && s.State == "Failed");
        Assert.False(await reservations.Outbox.AnyAsync(o => o.EffectKey == $"saga/{rejectedSaga.Id}/payment"));
    }
    [LocalFact]
    public async Task StaleAcceptedPriceReturnsConflictWithoutReservationWork()
    {
        var session = await ReservationTestSupport.SessionAsync(); var alice = await ReservationTestSupport.IdentityAsync("Alice");
        using var client = IntegrationTests.CreateClient(); var key = Guid.NewGuid().ToString();
        using var response = await Send(client, HttpMethod.Post, "", alice.Token,
            new CreateReservation(session.Id, 1, session.UnitAmountMinor + 1, "MXN"), key);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = ReservationTestSupport.Reservations();
        Assert.False(await db.Idempotency.AnyAsync(i => i.UserId == alice.Id && i.Key == key));
        Assert.False(await db.Reservations.AnyAsync(r => r.SessionId == session.Id));
    }
}
