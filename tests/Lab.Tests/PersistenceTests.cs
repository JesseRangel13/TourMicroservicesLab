using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Lab.Provisioner;
using Npgsql;

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
        var catalogBefore = await CatalogSnapshotAsync();
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
        // Preserve the evidence queue/message; never purge business queues during tests.
        var settings = IntegrationTests.Settings;
        await using var connection = new NpgsqlConnection(LocalConfiguration.Connection("localhost", "identity_runtime", settings.RuntimePasswords["identity"], Path.Combine(IntegrationTests.Root, ".local", "certificates")));
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM identity.\"AspNetUsers\"", connection);
        Assert.Equal(3L, await count.ExecuteScalarAsync());
        Assert.Equal(catalogBefore, await CatalogSnapshotAsync());
    }
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
                (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."DeliveryId"),'[]'::jsonb)::text FROM catalog."Outbox" t))
            """, connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Catalog snapshot missing."));
    }
    private static async Task<XDocument> BrokerAsync(HttpClient client, Dictionary<string, string> values)
    {
        values["Version"] = "2012-11-05";
        using var response = await client.PostAsync("", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
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
