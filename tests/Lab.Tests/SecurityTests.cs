using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using Gateway.Web.Identity;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;

namespace Lab.Tests;

public sealed class RateLimitFactAttribute : FactAttribute
{
    public RateLimitFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LAB_RATE_LIMIT_TEST") != "1")
            Skip = "Run Verify-Security.ps1 -RateLimitOnly last; it temporarily exhausts the local credential window.";
    }
}

[Collection("Local lab")]
public sealed class SecurityTests
{
    private static string Token(string scenario)
    {
        using var rsa = RSA.Create(2048);
        if (scenario != "signature") rsa.ImportFromPem(File.ReadAllText(Path.Combine(IntegrationTests.Root, ".local", "certificates", "jwt.key")));
        var claims = new List<Claim> { new("sub", "security-evidence"), new("role", "Tourist") };
        if (scenario == "missing-sub") claims.RemoveAll(c => c.Type == "sub");
        if (scenario == "blank-sub") claims[0] = new("sub", " ");
        if (scenario == "duplicate-sub") claims.Add(new("sub", "other-owner"));
        if (scenario == "missing-role") claims.RemoveAll(c => c.Type == "role");
        if (scenario == "unknown-role") claims[1] = new("role", "SuperAdmin");
        if (scenario == "duplicate-role") claims.Add(new("role", "Tourist"));
        if (scenario == "long-sub") claims[0] = new("sub", new string('x', 129));
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(scenario == "issuer" ? "tourlab-services" : "tourlab-identity",
            scenario == "audience" ? "catalog-internal" : "tourlab-api", claims,
            scenario == "future" ? now.AddMinutes(1) : now.AddMinutes(-1),
            scenario == "expired" ? now.AddSeconds(-1) : now.AddMinutes(scenario == "long-lived" ? 30 : 14),
            scenario == "unsigned" ? null : new SigningCredentials(new RsaSecurityKey(rsa) { CryptoProviderFactory = new() { CacheSignatureProviders = false } },
                scenario == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256));
        if (scenario == "missing-exp") jwt.Payload.Remove("exp");
        if (scenario == "missing-nbf") jwt.Payload.Remove("nbf");
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? token, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        // These intentionally spoofed headers must never change the validated principal.
        request.Headers.Add("X-User-Id", "other-owner"); request.Headers.Add("X-Role", "Admin");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return await client.SendAsync(request);
    }

    [LocalFact]
    public async Task EveryDirectServiceRejectsInvalidTrustLifetimeAndRequiredClaims()
    {
        using var client = IntegrationTests.CreateClient();
        foreach (var port in Enumerable.Range(8444, 4))
        {
            using var valid = await Send(client, HttpMethod.Get, $"https://localhost:{port}/v1/status", Token("valid"));
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            foreach (var scenario in new[] { "signature", "issuer", "audience", "expired", "future", "algorithm", "unsigned", "missing-exp", "missing-nbf", "missing-sub", "blank-sub", "long-sub", "duplicate-sub", "missing-role", "unknown-role", "duplicate-role", "long-lived" })
            {
                using var response = await Send(client, HttpMethod.Get, $"https://localhost:{port}/v1/status", Token(scenario));
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{port}/{scenario}: expected 401, got {(int)response.StatusCode}.");
                Assert.DoesNotContain("security-evidence", await response.Content.ReadAsStringAsync());
            }
        }
    }

    [LocalFact]
    public async Task RecoveryAndAdminMutationsRejectAnonymousAndSpoofedTouristAtTheBackend()
    {
        using var client = IntegrationTests.CreateClient(); var tourist = await ReservationTestSupport.IdentityAsync("Alice"); var id = Guid.NewGuid();
        var routes = new List<(HttpMethod Method, string Path, object? Body)>
        {
            (HttpMethod.Get, "reservations/v1/admin/sagas", null),
            (HttpMethod.Post, $"reservations/v1/admin/sagas/{id}/retry-compensation", null),
            (HttpMethod.Post, $"payments/v1/admin/payments/{id}/reconcile", null),
            (HttpMethod.Post, $"payments/v1/admin/fake-provider/{id}/resolve", new ProviderResolution("Succeeded"))
        };
        foreach (var service in new[] { "catalog", "reservations", "payments", "notifications" })
        {
            routes.Add((HttpMethod.Put, $"{service}/v1/admin/faults", new FaultSelection("None", 0)));
            routes.Add((HttpMethod.Post, $"{service}/v1/admin/faults/reset", null));
            routes.Add((HttpMethod.Post, $"{service}/v1/admin/dead-letters/{id}/replay", null));
        }
        foreach (var route in routes)
        foreach (var identity in new[] { (string?)null, tourist.Token })
        {
            using var response = await Send(client, route.Method, "https://localhost:8443/api/" + route.Path, identity, route.Body);
            Assert.Equal(identity is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [LocalFact]
    public async Task InternalRoutesRemainHiddenForMethodsCaseAndEncodedSegments()
    {
        using var client = IntegrationTests.CreateClient(); var admin = await ReservationTestSupport.IdentityAsync("Admin");
        using var direct = await Send(client, HttpMethod.Post, "https://localhost:8444/v1/internal/quotes", admin.Token, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, direct.StatusCode);
        foreach (var service in new[] { "catalog", "reservations", "payments", "notifications" })
        foreach (var segment in new[] { "internal/quotes", "INTERNAL/quotes", "%69nternal/quotes", "internal%2Fquotes", "internal/../internal/quotes" })
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var response = await Send(client, method, $"https://localhost:8443/api/{service}/v1/{segment}", admin.Token, new { });
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{service}/{segment}/{method}: expected 404, got {(int)response.StatusCode}.");
        }
    }

    [LocalFact]
    public async Task ConcurrentOwnersCannotReadFilterOrCancelEachOthersDurableResources()
    {
        using var client = IntegrationTests.CreateClient(); client.Timeout = TimeSpan.FromSeconds(20);
        var alice = await ReservationTestSupport.IdentityAsync("Alice"); var bob = await ReservationTestSupport.IdentityAsync("Bob");
        var admin = await ReservationTestSupport.IdentityAsync("Admin"); var session = await ReservationTestSupport.SessionAsync();
        var owners = new[] { alice, bob }; var accepted = new ReservationAccepted[2];
        // Real HTTP intentions plus actual enabled workers; body-selected identity is ignored.
        var pending = owners.Select(owner => Send(client, HttpMethod.Post, "https://localhost:8443/api/reservations/v1/reservations", owner.Token,
            new { sessionId = session.Id, participants = 1, expectedUnitAmountMinor = session.UnitAmountMinor, expectedCurrency = "MXN", userId = "other-owner", role = "Admin" }, Guid.NewGuid().ToString())).ToArray();
        var responses = await Task.WhenAll(pending);
        for (var i = 0; i < responses.Length; i++)
        {
            using var response = responses[i]; Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted[i] = (await response.Content.ReadFromJsonAsync<ReservationAccepted>())!;
        }
        for (var i = 0; i < owners.Length; i++)
        {
            Guid paymentId = default, notificationId = default;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var payments = PaymentWorkflowTests.Payments(); await using var notifications = PaymentWorkflowTests.Notifications();
                paymentId = await payments.Operations.Where(p => p.ReservationId == accepted[i].ReservationId).Select(p => p.Id).SingleOrDefaultAsync();
                notificationId = await notifications.Notifications.Where(n => n.ReservationId == accepted[i].ReservationId).Select(n => n.Id).SingleOrDefaultAsync();
                if (paymentId != default && notificationId != default) break;
                await Task.Delay(500);
            }
            Assert.NotEqual(Guid.Empty, paymentId); Assert.NotEqual(Guid.Empty, notificationId);
            var id = accepted[i].ReservationId; var foreign = owners[1 - i];
            foreach (var path in new[] { $"reservations/v1/reservations/{id}", $"reservations/v1/reservations/{id}/timeline", $"payments/v1/payments/{paymentId}", $"notifications/v1/notifications/{notificationId}" })
            {
                foreach (var viewer in new[] { owners[i], foreign, admin })
                {
                    using var response = await Send(client, HttpMethod.Get, "https://localhost:8443/api/" + path, viewer.Token);
                    Assert.Equal(viewer == foreign ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
                    if (viewer == owners[i] && !path.EndsWith("timeline", StringComparison.Ordinal))
                        Assert.Equal(owners[i].Id, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString());
                }
            }
            using var cancel = await Send(client, HttpMethod.Post, $"https://localhost:8443/api/reservations/v1/reservations/{id}/cancel", foreign.Token, key: Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
            using var filtered = await Send(client, HttpMethod.Get, $"https://localhost:8443/api/reservations/v1/reservations?userId={owners[i].Id}", foreign.Token);
            Assert.Equal(HttpStatusCode.Forbidden, filtered.StatusCode);
            foreach (var service in new[] { "payments", "notifications" })
            {
                using var list = await Send(client, HttpMethod.Get, $"https://localhost:8443/api/{service}/v1/{service}?reservationId={id}", foreign.Token);
                var json = await list.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Empty(service == "payments" ? json.EnumerateArray() : json.GetProperty("items").EnumerateArray());
            }
        }
    }

    [LocalFact]
    public async Task LoginIgnoresExternalReturnUrlsAndCookieAloneCannotMutateBusinessApis()
    {
        using var client = IntegrationTests.CreateClient(); var user = IntegrationTests.Settings.Users.Single(u => u.Name == "Admin");
        var html = await client.GetStringAsync("https://localhost:8443/login?returnUrl=https://example.invalid/");
        using var login = await client.PostAsync("https://localhost:8443/auth/login", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["username"] = user.Name, ["password"] = user.Password, ["returnUrl"] = "//example.invalid/", ["__RequestVerificationToken"] = IntegrationTests.ExtractAntiforgery(html) }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode); Assert.Equal("/", login.Headers.Location?.OriginalString);
        using var mutation = await client.PostAsJsonAsync("https://localhost:8443/api/catalog/v1/admin/tours", new CreateTour("Forbidden cookie mutation", ""));
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
    }

    [LocalFact]
    public async Task ExpiredEncryptedCookieIsRejectedWithoutWaitingThirtyMinutes()
    {
        // Test-only ticket fixture, encrypted using the real durable key ring and its private certificate.
        // This is not a public route or an extension of the production session lifetime.
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(IntegrationTests.Root, ".local", "certificates", "protection.pfx"), null);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddDbContextFactory<IdentityDb>(o => o.UseNpgsql(ReservationTestSupport.Connection("identity")));
        services.AddDataProtection().SetApplicationName("TourMicroservicesLab.Gateway").PersistKeysToDbContext<IdentityDb>().ProtectKeysWithCertificate(certificate);
        services.AddAuthentication().AddCookie(IdentityConstants.ApplicationScheme);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var user = await ReservationTestSupport.IdentityAsync("Alice");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, "Alice"),
            new(ClaimTypes.Role, "Tourist"), new("sessionExpires", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString())], IdentityConstants.ApplicationScheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) }, IdentityConstants.ApplicationScheme);
        using var client = IntegrationTests.CreateClient();
        using var valid = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8443/auth/me");
        valid.Headers.Add("Cookie", "__Host-tourlab=" + options.TicketDataFormat.Protect(ticket));
        using var accepted = await client.SendAsync(valid); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(user.Id, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString());
        ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1); ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-30);
        var cookie = options.TicketDataFormat.Protect(ticket);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8443/auth/me");
        request.Headers.Add("Cookie", "__Host-tourlab=" + cookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [RateLimitFact]
    public async Task CredentialLimiterRejectsChangingUntrustedForwardedAddresses()
    {
        using var client = IntegrationTests.CreateClient(); var limited = false;
        for (var i = 0; i <= 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost:8443/auth/token");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            request.Content = JsonContent.Create(new { username = "nonexistent-rate-test-" + Guid.NewGuid(), password = "fictional-invalid-password" });
            using var response = await client.SendAsync(request);
            if ((int)response.StatusCode == 429) { limited = true; break; }
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.True(limited, "Direct connection IP must remain the credential partition; X-Forwarded-For is untrusted.");
        using var loginPage = await client.GetAsync("https://localhost:8443/login");
        var csrf = IntegrationTests.ExtractAntiforgery(await loginPage.Content.ReadAsStringAsync());
        using var login = await client.PostAsync("https://localhost:8443/auth/login", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["username"] = "nonexistent-rate-test", ["password"] = "fictional", ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(429, (int)login.StatusCode);
    }
}
