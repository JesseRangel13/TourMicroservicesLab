using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lab.Provisioner;
using Npgsql;

namespace Lab.Tests;

[CollectionDefinition("Local lab", DisableParallelization = true)]
public sealed class LocalLabCollection;

public sealed class LocalFactAttribute : FactAttribute
{
    public LocalFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAB_TEST_ROOT")))
            Skip = "Set LAB_TEST_ROOT and start the real local lab with scripts/Start-Local.ps1.";
    }
}

[Collection("Local lab")]
public sealed class IntegrationTests
{
    internal static string Root => Environment.GetEnvironmentVariable("LAB_TEST_ROOT") ?? throw new InvalidOperationException("LAB_TEST_ROOT required.");
    internal static ProvisioningSettings Settings => JsonSerializer.Deserialize<ProvisioningSettings>(File.ReadAllText(Path.Combine(Root, ".local", "provisioner.json")))
        ?? throw new InvalidOperationException("Private settings missing.");

    [LocalFact]
    public async Task EveryRuntimeRoleCanReadOwnSchemaButCannotReadWriteOrCreateElsewhere()
    {
        var settings = Settings;
        foreach (var schema in LocalConfiguration.Schemas)
        {
            await using var connection = new NpgsqlConnection(LocalConfiguration.Connection("localhost", $"{schema}_runtime", settings.RuntimePasswords[schema], Path.Combine(Root, ".local", "certificates")));
            await connection.OpenAsync();
            await using (var command = new NpgsqlCommand($"SELECT \"Version\" FROM {schema}.\"SchemaVersion\"", connection))
                Assert.Equal(1, await command.ExecuteScalarAsync());
            foreach (var other in LocalConfiguration.Schemas.Where(s => s != schema))
            {
                foreach (var sql in new[] { $"SELECT * FROM {other}.\"SchemaVersion\"", $"UPDATE {other}.\"SchemaVersion\" SET \"Version\"=\"Version\"" })
                {
                    await using var denied = new NpgsqlCommand(sql, connection);
                    var exception = await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteNonQueryAsync());
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
                }
            }
            await using var create = new NpgsqlCommand($"CREATE TABLE {schema}.\"UnauthorizedTable\" (id int)", connection);
            var createError = await Assert.ThrowsAsync<PostgresException>(async () => await create.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, createError.SqlState);
            await using var publicCreate = new NpgsqlCommand("CREATE TABLE public.\"UnauthorizedTable\" (id int)", connection);
            var publicError = await Assert.ThrowsAsync<PostgresException>(async () => await publicCreate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, publicError.SqlState);
        }
    }

    [LocalFact]
    public async Task SeededUsersHavePasswordHashesAndExpectedRoles()
    {
        var settings = Settings;
        await using var connection = new NpgsqlConnection(LocalConfiguration.Connection("localhost", "identity_runtime", settings.RuntimePasswords["identity"], Path.Combine(Root, ".local", "certificates")));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT u."UserName", u."PasswordHash", r."Name" FROM identity."AspNetUsers" u
            JOIN identity."AspNetUserRoles" ur ON ur."UserId"=u."Id"
            JOIN identity."AspNetRoles" r ON r."Id"=ur."RoleId"
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var found = new HashSet<string>();
        while (await reader.ReadAsync())
        {
            var seed = settings.Users.Single(u => u.Name == reader.GetString(0));
            Assert.NotEqual(seed.Password, reader.GetString(1));
            Assert.Equal(seed.Role, reader.GetString(2));
            found.Add(seed.Name);
        }
        Assert.Equal(3, found.Count);
    }

    [LocalFact]
    public async Task PostgreSqlRejectsPlaintextRuntimeConnections()
    {
        var settings = Settings;
        var connectionString = new NpgsqlConnectionStringBuilder(LocalConfiguration.Connection("localhost", "catalog_runtime", settings.RuntimePasswords["catalog"], Path.Combine(Root, ".local", "certificates"))) { SslMode = SslMode.Disable };
        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => connection.OpenAsync());
    }

    [LocalFact]
    public async Task ElasticMqHasFourStandardQueuesWithRequiredVisibilityAndDeadLetterPolicy()
    {
        using var broker = new HttpClient { BaseAddress = new Uri("http://localhost:9324/"), Timeout = TimeSpan.FromSeconds(5) };
        using var list = await broker.PostAsync("", new FormUrlEncodedContent(new Dictionary<string, string> { ["Action"] = "ListQueues", ["Version"] = "2012-11-05" }));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var xml = System.Xml.Linq.XDocument.Parse(await list.Content.ReadAsStringAsync());
        var urls = xml.Descendants().Where(e => e.Name.LocalName == "QueueUrl").Select(e => e.Value).ToArray();
        foreach (var schema in LocalConfiguration.Schemas.Take(4))
        {
            var queue = urls.Single(u => u.EndsWith($"/tourlab-{schema}", StringComparison.Ordinal));
            Assert.Contains(urls, u => u.EndsWith($"/tourlab-{schema}-dlq", StringComparison.Ordinal));
            using var attributes = await broker.PostAsync("", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Action"] = "GetQueueAttributes", ["Version"] = "2012-11-05", ["QueueUrl"] = queue, ["AttributeName.1"] = "All" }));
            Assert.Equal(HttpStatusCode.OK, attributes.StatusCode);
            var values = System.Xml.Linq.XDocument.Parse(await attributes.Content.ReadAsStringAsync()).Descendants()
                .Where(e => e.Name.LocalName == "Attribute").ToDictionary(
                    e => e.Elements().Single(x => x.Name.LocalName == "Name").Value,
                    e => e.Elements().Single(x => x.Name.LocalName == "Value").Value);
            Assert.Equal("60", values["VisibilityTimeout"]);
            Assert.Equal("20", values["ReceiveMessageWaitTimeSeconds"]);
            var policy = JsonDocument.Parse(values["RedrivePolicy"]);
            Assert.Equal("5", policy.RootElement.GetProperty("maxReceiveCount").ToString());
            Assert.Contains($"tourlab-{schema}-dlq", policy.RootElement.GetProperty("deadLetterTargetArn").GetString());
        }
    }

    [LocalFact]
    public async Task JwtLoginAndFiveHostsEnforceAuthenticationAndAdminRole()
    {
        using var client = CreateClient();
        var settings = Settings;
        foreach (var seed in settings.Users)
        {
            using var login = await client.PostAsJsonAsync("https://localhost:8443/auth/token", new { username = seed.Name, password = seed.Password, role = "Admin" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var json = await login.Content.ReadFromJsonAsync<JsonElement>();
            var token = json.GetProperty("accessToken").GetString();
            Assert.Equal(900, json.GetProperty("expiresIn").GetInt32());
            for (var i = 0; i < 4; i++)
            {
                using var direct = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{8444 + i}/v1/status");
                direct.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(direct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var proxied = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:8443/api/{LocalConfiguration.Schemas[i]}/v1/admin/status");
                proxied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var adminResponse = await client.SendAsync(proxied);
                Assert.Equal(seed.Role == "Admin" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, adminResponse.StatusCode);
            }
        }
        using var missing = await client.GetAsync("https://localhost:8444/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8444/v1/status");
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var badResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, badResponse.StatusCode);
        using var internalRoute = await client.GetAsync("https://localhost:8443/api/catalog/v1/internal/quotes");
        Assert.Equal(HttpStatusCode.NotFound, internalRoute.StatusCode);
    }

    [LocalFact]
    public async Task CookieLoginAndLogoutRequireAntiforgeryAndUseSecureCookies()
    {
        using var client = CreateClient();
        var seed = Settings.Users.Single(u => u.Name == "Alice");
        using var forged = await client.PostAsync("https://localhost:8443/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = seed.Name, ["password"] = seed.Password }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        var loginHtml = await client.GetStringAsync("https://localhost:8443/login");
        var csrf = ExtractAntiforgery(loginHtml);
        using var login = await client.PostAsync("https://localhost:8443/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = seed.Name, ["password"] = seed.Password, ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-tourlab=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        using var me = await client.GetAsync("https://localhost:8443/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var account = await client.GetStringAsync("https://localhost:8443/account");
        Assert.Contains("Signed in as Alice", account);
        using var forgedLogout = await client.PostAsync("https://localhost:8443/auth/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, forgedLogout.StatusCode);
        var logoutHtml = await client.GetStringAsync("https://localhost:8443/logout");
        using var logout = await client.PostAsync("https://localhost:8443/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = ExtractAntiforgery(logoutHtml) }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var after = await client.GetAsync("https://localhost:8443/auth/me");
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    [LocalFact]
    public async Task DataProtectionKeysAreEncryptedAtRest()
    {
        using var client = CreateClient();
        await client.GetAsync("https://localhost:8443/login");
        var settings = Settings;
        await using var connection = new NpgsqlConnection(LocalConfiguration.Connection("localhost", "identity_runtime", settings.RuntimePasswords["identity"], Path.Combine(Root, ".local", "certificates")));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"Xml\" FROM identity.\"DataProtectionKeys\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync()) { Assert.Contains("encryptedSecret", reader.GetString(0)); count++; }
        Assert.True(count > 0);
    }

    internal static string ExtractAntiforgery(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "SSR form must include an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    internal static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        var ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(Root, ".local", "certificates", "ca.crt"));
        // Explicit trust of this lab CA; hostname, validity, and signatures are still verified.
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // local CA has no revocation endpoint
            return chain.Build(certificate);
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}
