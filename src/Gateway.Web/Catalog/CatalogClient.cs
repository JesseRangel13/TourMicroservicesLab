using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gateway.Web.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Shared.Contracts;
namespace Gateway.Web.Catalog;

public sealed class GatewayApiOptions
{
    public string BaseAddress { get; set; } = "";
}
public sealed class CatalogClient(IHttpClientFactory factory, AuthenticationStateProvider authentication,
    JwtIssuer issuer, IOptions<GatewayApiOptions> options)
{
    public Task<TourPage> SearchAsync(string search, int page, CancellationToken ct) => SendAsync<TourPage>(HttpMethod.Get,
        $"tours?search={Uri.EscapeDataString(search)}&page={page}&pageSize=10", null, ct);
    public Task<TourView> DetailAsync(Guid id, CancellationToken ct) => SendAsync<TourView>(HttpMethod.Get, $"tours/{id}", null, ct);
    public Task<SessionView[]> SessionsAsync(Guid id, CancellationToken ct) => SendAsync<SessionView[]>(HttpMethod.Get, $"tours/{id}/sessions", null, ct);
    public Task<TourView> CreateAsync(CreateTour command, CancellationToken ct) => SendAsync<TourView>(HttpMethod.Post, "admin/tours", command, ct);
    public Task<TourView> UpdateAsync(Guid id, UpdateTour command, CancellationToken ct) => SendAsync<TourView>(HttpMethod.Put, $"admin/tours/{id}", command, ct);
    public Task<SessionView> CreateSessionAsync(Guid id, CreateSession command, CancellationToken ct) => SendAsync<SessionView>(HttpMethod.Post, $"admin/tours/{id}/sessions", command, ct);
    public Task<SessionView> PriceAsync(Guid id, UpdatePrice command, CancellationToken ct) => SendAsync<SessionView>(HttpMethod.Put, $"admin/sessions/{id}/price", command, ct);
    public Task<SessionView> CapacityAsync(Guid id, UpdateCapacity command, CancellationToken ct) => SendAsync<SessionView>(HttpMethod.Put, $"admin/sessions/{id}/capacity", command, ct);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId)
            || !long.TryParse(principal.FindFirstValue("sessionExpires"), out var expiry) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry)
            throw new CatalogClientException("Your session has expired. Sign in again.");
        using var client = factory.CreateClient("GatewayApi");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.Value.BaseAddress), $"api/catalog/v1/{path}"));
        // A new message and bearer token for this circuit's verified principal; no shared default headers.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue(userId, principal.Identity.Name ?? "",
            principal.FindAll(ClaimTypes.Role).Select(c => c.Value)));
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = "RequestFailed";
            if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (problem.TryGetProperty("code", out var value)) code = value.GetString() ?? code;
            }
            throw new CatalogClientException($"{code} (HTTP {(int)response.StatusCode}). Reload current data after a conflict.");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new CatalogClientException("Empty response.");
    }
}
public sealed class CatalogClientException(string message) : Exception(message);
