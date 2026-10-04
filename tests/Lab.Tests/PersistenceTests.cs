using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Lab.Provisioner;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace Lab.Tests;

public sealed class RestartFactAttribute : FactAttribute
{
    public RestartFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LAB_RESTART_TEST") != "1")
            Skip = "Use scripts/Verify-Restart.ps1 for controlled local stop/start persistence.";
    }
}
[Collection("Local lab")]
public sealed class PersistenceTests
{
    [RestartFact]
    public async Task DatabaseQueueMessageAndLoginCookieSurviveLocalStopStart()
    {
        using var client = IntegrationTests.CreateClient();
        using var broker = new HttpClient { BaseAddress = new Uri("http://localhost:9324/"), Timeout = TimeSpan.FromSeconds(10) };
        var queueName = "tourlab-evidence-" + Guid.NewGuid().ToString("N");
        var create = await BrokerAsync(broker, new() { ["Action"] = "CreateQueue", ["QueueName"] = queueName });
        var queueUrl = create.Descendants().Single(e => e.Name.LocalName == "QueueUrl").Value;
        var marker = Guid.NewGuid().ToString();
        await BrokerAsync(broker, new() { ["Action"] = "SendMessage", ["QueueUrl"] = queueUrl, ["MessageBody"] = marker });
        var seed = IntegrationTests.Settings.Users.Single(u => u.Name == "Bob");
        var html = await client.GetStringAsync("https://localhost:8443/login");
        using var login = await client.PostAsync("https://localhost:8443/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = seed.Name, ["password"] = seed.Password, ["__RequestVerificationToken"] = IntegrationTests.ExtractAntiforgery(html) }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var before = await client.GetAsync("https://localhost:8443/auth/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeIdentity = await before.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // Component-level accepted Pending work, not a fabricated successful reservation workflow.
        var pendingPayment = Guid.NewGuid(); var pendingNotificationEvent = Guid.NewGuid();
        await using (var db = PaymentWorkflowTests.Payments())
            await new Payments.Api.Application.PaymentConsumer(db).ConsumeAsync(Shared.Infrastructure.Messaging.MessageCodec.Parse(
                Shared.Infrastructure.Messaging.MessageRoutes.Serialize(new Shared.Contracts.ProcessPayment(pendingPayment, Guid.NewGuid(), "restart-evidence", 100, "MXN"),
                    Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())), default);
        await using (var db = PaymentWorkflowTests.Notifications())
            await new Notifications.Api.Application.NotificationConsumer(db, TimeProvider.System).ConsumeAsync(Shared.Infrastructure.Messaging.MessageCodec.Parse(
                Shared.Infrastructure.Messaging.MessageRoutes.Serialize(new Shared.Contracts.ReservationConfirmed(Guid.NewGuid(), "restart-evidence", "Simulated sender component restart evidence"),
                    pendingNotificationEvent, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())), default);
        Guid pendingNotification;
        await using (var db = PaymentWorkflowTests.Notifications())
            pendingNotification = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Notifications, n => n.SourceEventId == pendingNotificationEvent)).Id;
        await using var sagaLab=new MessagingTests.Lab();await sagaLab.InitAsync();
        var repairing=await SagaWorkflowTests.RestartCompensation(sagaLab);
        var repairingSaga=await SagaWorkflowTests.Saga(repairing.Accepted.SagaId);
        var catalogBefore = await CatalogSnapshotAsync();
        var reservationsBefore = await ReservationsSnapshotAsync();
        var paymentsBefore = await WorkSnapshotAsync("payments", ["Operations", "ProviderEffects", "RefundOperations", "ProviderRefunds", "Inbox", "Outbox", "Faults", "Audit"]);
        var notificationsBefore = await WorkSnapshotAsync("notifications", ["Notifications", "DeliveryReceipts", "Inbox", "Outbox", "Faults", "Audit"]);
        await DockerAsync("stop");
        await DockerAsync("start");
        var alive = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var health = await client.GetAsync("https://localhost:8443/health/live");
                using var queues = await broker.PostAsync("", new FormUrlEncodedContent(new Dictionary<string, string> { ["Action"] = "ListQueues", ["Version"] = "2012-11-05" }));
                if (health.IsSuccessStatusCode && queues.IsSuccessStatusCode) { alive = true; break; }
            }
            catch (HttpRequestException) { }
            await Task.Delay(500);
        }
        Assert.True(alive, "Owned containers must restart within 30 seconds after start completes.");
        using var after = await client.GetAsync("https://localhost:8443/auth/me");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var afterIdentity = await after.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(beforeIdentity.GetProperty("userId").GetString(), afterIdentity.GetProperty("userId").GetString());
        var received = await BrokerAsync(broker, new() { ["Action"] = "ReceiveMessage", ["QueueUrl"] = queueUrl, ["WaitTimeSeconds"] = "1" });
        Assert.Equal(marker, received.Descendants().Single(e => e.Name.LocalName == "Body").Value);
        // This harness survives the restart; production processes do not. Discard pre-restart physical sockets before ANY pooled query.
        NpgsqlConnection.ClearAllPools();
        // Preserve the evidence queue/message; never purge business queues during tests.
        var settings = IntegrationTests.Settings;
        await using var connection = new NpgsqlConnection(LocalConfiguration.Connection("localhost", "identity_runtime", settings.RuntimePasswords["identity"], Path.Combine(IntegrationTests.Root, ".local", "certificates")));
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM identity.\"AspNetUsers\"", connection);
        Assert.Equal(3L, await count.ExecuteScalarAsync());
        Assert.Equal(catalogBefore, await CatalogSnapshotAsync());
        Assert.Equal(reservationsBefore, await ReservationsSnapshotAsync());
        Assert.Equal(paymentsBefore, await WorkSnapshotAsync("payments", ["Operations", "ProviderEffects", "RefundOperations", "ProviderRefunds", "Inbox", "Outbox", "Faults", "Audit"]));
        Assert.Equal(notificationsBefore, await WorkSnapshotAsync("notifications", ["Notifications", "DeliveryReceipts", "Inbox", "Outbox", "Faults", "Audit"]));
        var recoveryClock=new SagaWorkflowTests.Clock(DateTimeOffset.UtcNow.AddSeconds(61));
        Assert.True(await SagaWorkflowTests.RefundProcess(repairingSaga.RefundOperationId,recoveryClock));
        await SagaWorkflowTests.RefundResult(sagaLab,repairingSaga.RefundOperationId);
        var repaired=await SagaWorkflowTests.Saga(repairingSaga.Id);
        Assert.Equal("Failed",repaired.State);Assert.True(repaired.RefundCompleted && repaired.SeatsReleased);
        await using(var db=FreshPayments())Assert.Equal(1,await db.ProviderRefunds.CountAsync(x=>x.Id==repairingSaga.RefundOperationId && x.Reference!=null));
        await using (var db = FreshPayments())
            Assert.True(await new Payments.Api.Application.PaymentProcessor(db,
                new Payments.Api.Application.DurableFakeProvider(new PaymentWorkflowTests.Factory<Payments.Api.Persistence.PaymentsDb>(FreshPayments), TimeProvider.System),
                TimeProvider.System).ProcessAsync(default, pendingPayment));
        await using (var db = FreshNotifications())
            Assert.True(await new Notifications.Api.Application.NotificationProcessor(db,
                new Notifications.Api.Application.DurableFakeSender(new PaymentWorkflowTests.Factory<Notifications.Api.Persistence.NotificationsDb>(FreshNotifications), TimeProvider.System),
                TimeProvider.System).ProcessAsync(default, pendingNotification));
        await using (var db = FreshPayments()) Assert.NotEqual("Pending", (await db.Operations.SingleAsync(o => o.Id == pendingPayment)).Status);
        await using (var db = FreshNotifications()) Assert.True(await db.Notifications.AnyAsync(n => n.Id == pendingNotification));
    }
    // The harness survives stop/start; restarted hosts have new pools. These recovery contexts emulate new connections explicitly.
    private static Payments.Api.Persistence.PaymentsDb FreshPayments() => new(new DbContextOptionsBuilder<Payments.Api.Persistence.PaymentsDb>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(ReservationTestSupport.Connection("payments")) { Pooling = false }.ConnectionString).Options);
    private static Notifications.Api.Persistence.NotificationsDb FreshNotifications() => new(new DbContextOptionsBuilder<Notifications.Api.Persistence.NotificationsDb>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(ReservationTestSupport.Connection("notifications")) { Pooling = false }.ConnectionString).Options);
    private static async Task<string> CatalogSnapshotAsync()
    {
        var settings = IntegrationTests.Settings;
        // The test process survives while PostgreSQL stops. Probe with a new physical connection,
        // rather than reuse a socket from its pre-restart pool; restarted hosts also have fresh pools.
        var probe = new NpgsqlConnectionStringBuilder(LocalConfiguration.Connection("localhost", "catalog_runtime",
            settings.RuntimePasswords["catalog"], Path.Combine(IntegrationTests.Root, ".local", "certificates"))) { Pooling = false };
        await using var connection = new NpgsqlConnection(probe.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT md5(
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM catalog."Tours" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM catalog."Sessions" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM catalog."Quotes" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM catalog."Holds" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."DeliveryId"),'[]'::jsonb)::text FROM catalog."Outbox" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."ConsumerName",t."MessageId"),'[]'::jsonb)::text FROM catalog."Inbox" t))
            """, connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Catalog snapshot missing."));
    }
    private static async Task<string> ReservationsSnapshotAsync()
    {
        var settings = IntegrationTests.Settings;
        var probe = new NpgsqlConnectionStringBuilder(LocalConfiguration.Connection("localhost", "reservations_runtime",
            settings.RuntimePasswords["reservations"], Path.Combine(IntegrationTests.Root, ".local", "certificates"))) { Pooling = false };
        await using var connection = new NpgsqlConnection(probe.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT md5(
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM reservations."Reservations" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM reservations."Sagas" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."UserId",t."OperationName",t."Key"),'[]'::jsonb)::text FROM reservations."IdempotencyRequests" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."Id"),'[]'::jsonb)::text FROM reservations."History" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."DeliveryId"),'[]'::jsonb)::text FROM reservations."Outbox" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."ConsumerName",t."MessageId"),'[]'::jsonb)::text FROM reservations."Inbox" t) ||
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."SessionId"),'[]'::jsonb)::text FROM reservations."TourProjections" t))
            """, connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Reservations snapshot missing."));
    }
    private static async Task<XDocument> BrokerAsync(HttpClient client, Dictionary<string, string> values)
    {
        values["Version"] = "2012-11-05";
        using var response = await client.PostAsync("", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private static async Task<string> WorkSnapshotAsync(string schema, string[] tables)
    {
        // Fixed test-owned identifiers only; every connection uses that service's isolated runtime role.
        var connectionOptions = new NpgsqlConnectionStringBuilder(ReservationTestSupport.Connection(schema)) { Pooling = false };
        await using var connection = new NpgsqlConnection(connectionOptions.ConnectionString); await connection.OpenAsync();
        var pieces = tables.Select(table => $"(SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text FROM {schema}.\"{table}\" t)");
        await using var command = new NpgsqlCommand("SELECT md5(" + string.Join(" || ", pieces) + ")", connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Work snapshot missing."));
    }
    private static async Task DockerAsync(string operation)
    {
        var start = new ProcessStartInfo("docker") { WorkingDirectory = IntegrationTests.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("compose"); start.ArgumentList.Add(operation);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(output, errors);
        Assert.Equal(0, process.ExitCode);
    }
}
