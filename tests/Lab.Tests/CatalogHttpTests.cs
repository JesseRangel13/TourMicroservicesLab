using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
namespace Lab.Tests;

[Collection("Local lab")]
public sealed class CatalogHttpTests
{
    private static async Task<string> Login(HttpClient client, string name)
    {
        var user = IntegrationTests.Settings.Users.Single(u => u.Name == name);
        using var response = await client.PostAsJsonAsync("https://localhost:8443/auth/token", new { username = user.Name, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token, object? body = null, bool gateway = true)
    {
        using var request = new HttpRequestMessage(method, (gateway ? "https://localhost:8443/api/catalog/v1/" : "https://localhost:8444/v1/") + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return await client.SendAsync(request);
    }
    private static string ServiceToken(string subject = "reservations", string permission = "quote:create", string audience = "catalog-internal",
        string issuer = "tourlab-services", int minutes = 2, bool wrongKey = false, bool expired = false)
    {
        using var rsa = RSA.Create(3072);
        if (!wrongKey) rsa.ImportFromPem(File.ReadAllText(Path.Combine(IntegrationTests.Root, ".local", "certificates", "reservations-signing.key")));
        var now = DateTime.UtcNow.AddSeconds(-1); if (expired) now = now.AddMinutes(-5);
        var jwt = new JwtSecurityToken(issuer, audience, [new Claim("sub", subject), new Claim("permission", permission)], now, now.AddMinutes(minutes),
            new SigningCredentials(new RsaSecurityKey(rsa) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
    [LocalFact]
    public async Task GatewayCatalogCrudPaginationAndProblemDetailsUseAuthenticatedAdmin()
    {
        using var client = IntegrationTests.CreateClient(); var token = await Login(client, "Admin");
        var name = "HTTP evidence " + Guid.NewGuid();
        using var created = await Send(client, HttpMethod.Post, "admin/tours", token, new CreateTour(name, "HTTP integration test."));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var tour = (await created.Content.ReadFromJsonAsync<TourView>())!;
        using var sessionCreated = await Send(client, HttpMethod.Post, $"admin/tours/{tour.Id}/sessions", token, new CreateSession(DateTimeOffset.UtcNow.AddDays(4), 45000, 5));
        Assert.Equal(HttpStatusCode.Created, sessionCreated.StatusCode); var session = (await sessionCreated.Content.ReadFromJsonAsync<SessionView>())!;
        using var page = await Send(client, HttpMethod.Get, $"tours?search={Uri.EscapeDataString(name)}&page=1&pageSize=1", token);
        var result = (await page.Content.ReadFromJsonAsync<TourPage>())!; Assert.Equal(1, result.Total); Assert.Single(result.Items);
        using var detail = await Send(client, HttpMethod.Get, $"tours/{tour.Id}", token); Assert.Equal(tour, await detail.Content.ReadFromJsonAsync<TourView>());
        using var sessions = await Send(client, HttpMethod.Get, $"tours/{tour.Id}/sessions", token); Assert.Single((await sessions.Content.ReadFromJsonAsync<SessionView[]>())!);
        using var price = await Send(client, HttpMethod.Put, $"admin/sessions/{session.Id}/price", token, new UpdatePrice(55000, session.Version));
        Assert.Equal(HttpStatusCode.OK, price.StatusCode); var priced = (await price.Content.ReadFromJsonAsync<SessionView>())!;
        using var conflict = await Send(client, HttpMethod.Put, $"admin/sessions/{session.Id}/capacity", token, new UpdateCapacity(6, session.Version));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VersionConflict", problem.GetProperty("code").GetString()); Assert.True(problem.TryGetProperty("traceId", out _));
        using var capacity = await Send(client, HttpMethod.Put, $"admin/sessions/{session.Id}/capacity", token, new UpdateCapacity(6, priced.Version)); Assert.Equal(HttpStatusCode.OK, capacity.StatusCode);
        using var update = await Send(client, HttpMethod.Put, $"admin/tours/{tour.Id}", token, new UpdateTour(name + " edited", "Changed", false, tour.Version)); Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var invalid = await Send(client, HttpMethod.Get, "tours?page=0", token); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidJson = await Send(client, HttpMethod.Post, "admin/tours", token, new CreateTour(null, null)); Assert.Equal(HttpStatusCode.BadRequest, invalidJson.StatusCode);
        using var missingActive = await Send(client, HttpMethod.Put, $"admin/tours/{tour.Id}", token,
            new { name, description = "Missing required active field", expectedVersion = 2 }); Assert.Equal(HttpStatusCode.BadRequest, missingActive.StatusCode);
        using var missing = await Send(client, HttpMethod.Get, "tours", null); Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }
    [LocalFact]
    public async Task TouristCannotMutateCatalogAndNoPublicInventoryRoutesExist()
    {
        using var client = IntegrationTests.CreateClient(); var token = await Login(client, "Alice"); var id = Guid.NewGuid();
        foreach (var (method, path, body) in new (HttpMethod, string, object)[] {
            (HttpMethod.Post, "admin/tours", new CreateTour("Unauthorized", "")),
            (HttpMethod.Put, $"admin/tours/{id}", new UpdateTour("Unauthorized", "", false, 1)),
            (HttpMethod.Post, $"admin/tours/{id}/sessions", new CreateSession(DateTimeOffset.UtcNow.AddDays(1), 1, 1)),
            (HttpMethod.Put, $"admin/sessions/{id}/price", new UpdatePrice(1, 1)),
            (HttpMethod.Put, $"admin/sessions/{id}/capacity", new UpdateCapacity(1, 1)) })
        { using var denied = await Send(client, method, path, token, body); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        foreach (var path in new[] { "internal/holds", "internal/confirm", "internal/release", "internal/expire" })
        { using var absent = await Send(client, HttpMethod.Post, path, token, new { }, gateway: false); Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode); }
    }
    [LocalFact]
    public async Task InternalQuotesRequireDedicatedReservationsIdentityAndAreBlockedByGateway()
    {
        using var client = IntegrationTests.CreateClient();
        var body = new CreateQuote(Guid.NewGuid(), Guid.Parse("20000000-0000-0000-0000-000000000001"), 1, "service-http-test", 25000, "MXN");
        using var valid = await Send(client, HttpMethod.Post, "internal/quotes", ServiceToken(), body, false); Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var quote = (await valid.Content.ReadFromJsonAsync<QuoteView>())!;
        using var repeat = await Send(client, HttpMethod.Post, "internal/quotes", ServiceToken(), body, false);
        Assert.Equal(quote.QuoteId, (await repeat.Content.ReadFromJsonAsync<QuoteView>())!.QuoteId);
        foreach (var token in new[] { (string?)null, ServiceToken(wrongKey: true), ServiceToken(audience: "tourlab-api"), ServiceToken(issuer: "tourlab-identity"), ServiceToken(minutes: 3), ServiceToken(expired: true) })
        { using var denied = await Send(client, HttpMethod.Post, "internal/quotes", token, body, false); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        foreach (var token in new[] { ServiceToken(subject: "payments"), ServiceToken(permission: "other") })
        { using var denied = await Send(client, HttpMethod.Post, "internal/quotes", token, body, false); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using var hidden = await Send(client, HttpMethod.Post, "internal/quotes", ServiceToken(), body); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var noHuman = await Send(client, HttpMethod.Get, "tours", ServiceToken(), gateway: false); Assert.Equal(HttpStatusCode.Unauthorized, noHuman.StatusCode);
    }
    [LocalFact]
    public async Task CatalogScreensPrerenderThroughGatewayWithCookieIdentity()
    {
        // HTTP/SSR evidence only: this does not execute a browser circuit or click interactive controls.
        using var client = IntegrationTests.CreateClient(); var user = IntegrationTests.Settings.Users.Single(u => u.Name == "Admin");
        var html = await client.GetStringAsync("https://localhost:8443/login");
        using var login = await client.PostAsync("https://localhost:8443/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["username"] = user.Name, ["password"] = user.Password, ["__RequestVerificationToken"] = IntegrationTests.ExtractAntiforgery(html) })); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var tours = await client.GetStringAsync("https://localhost:8443/tours"); Assert.Contains("Previous", tours); Assert.Contains("Manage catalog", tours); Assert.DoesNotContain("Catalog is unavailable", tours);
        var detail = await client.GetStringAsync("https://localhost:8443/tours/10000000-0000-0000-0000-000000000001"); Assert.Contains("Payments and notification delivery are simulated", detail);
        var edit = await client.GetStringAsync("https://localhost:8443/admin/catalog/10000000-0000-0000-0000-000000000001"); Assert.Contains("Save price", edit); Assert.Contains("Save capacity", edit);
        Assert.Contains("Simulated payments", await client.GetStringAsync("https://localhost:8443/payments"));
        Assert.Contains("Receive up to 10",await client.GetStringAsync("https://localhost:8443/operations"));
        Assert.Contains("Informational tour projection",await client.GetStringAsync("https://localhost:8443/tour-projections"));
        Assert.Contains("Simulated notifications", await client.GetStringAsync("https://localhost:8443/notifications"));
        Assert.Contains("Bounded simulated controls", await client.GetStringAsync("https://localhost:8443/admin/simulations"));
    }
}
