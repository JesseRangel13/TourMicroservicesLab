using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gateway.Web.Catalog;
using Gateway.Web.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Shared.Contracts;
namespace Gateway.Web.Reservations;

public sealed class ReservationClient(IHttpClientFactory factory, AuthenticationStateProvider authentication,
    JwtIssuer issuer, IOptions<GatewayApiOptions> options)
{
    public Task<ReservationAccepted> CreateAsync(CreateReservation command, string key, CancellationToken ct) => SendAsync<ReservationAccepted>(HttpMethod.Post, "", command, key, ct);
    public Task<ReservationPage> ListAsync(int page, string? userId, string? status, CancellationToken ct) => SendAsync<ReservationPage>(HttpMethod.Get,
        $"?page={page}&pageSize=10{(string.IsNullOrEmpty(userId) ? "" : "&userId=" + Uri.EscapeDataString(userId))}{(string.IsNullOrEmpty(status) ? "" : "&status=" + Uri.EscapeDataString(status))}", null, null, ct);
    public Task<ReservationView> DetailAsync(Guid id, CancellationToken ct) => SendAsync<ReservationView>(HttpMethod.Get, $"/{id}", null, null, ct);
    public Task<ReservationAccepted> CancelAsync(Guid id,string key,CancellationToken ct)=>SendAsync<ReservationAccepted>(HttpMethod.Post,$"/{id}/cancel",null,key,ct);
    public Task<TransitionView[]> TimelineAsync(Guid id, CancellationToken ct) => SendAsync<TransitionView[]>(HttpMethod.Get, $"/{id}/timeline", null, null, ct);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? key, CancellationToken ct)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(id) || !long.TryParse(user.FindFirstValue("sessionExpires"), out var expires)
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expires) throw new CatalogClientException("Your session has expired. Sign in again.");
        using var client = factory.CreateClient("GatewayApi");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.Value.BaseAddress), "api/reservations/v1/reservations" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue(id, user.Identity.Name ?? "", user.FindAll(ClaimTypes.Role).Select(c => c.Value)));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = "RequestFailed";
            if (response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
            { var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct); if (problem.TryGetProperty("code", out var value)) code = value.GetString() ?? code; }
            throw new CatalogClientException($"{code} (HTTP {(int)response.StatusCode}). Retry an uncertain submission with the same intention key; confirm changed prices before a new intention.");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new CatalogClientException("Empty response.");
    }
}
