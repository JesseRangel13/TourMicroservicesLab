using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
namespace Reservations.Api.Application;

public sealed class CatalogClientOptions
{
    public string BaseAddress { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 3;
}
public sealed class ServiceTokenIssuer : IDisposable
{
    private readonly RSA rsa = RSA.Create();
    public ServiceTokenIssuer(IOptions<CatalogClientOptions> options) => rsa.ImportFromPem(File.ReadAllText(options.Value.PrivateKeyPath));
    public string Issue()
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("tourlab-services", "catalog-internal",
            [new Claim("sub", "reservations"), new Claim("permission", "quote:create")], now, now.AddMinutes(2),
            new SigningCredentials(new RsaSecurityKey(rsa) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } }, SecurityAlgorithms.RsaSha256)));
    }
    public void Dispose() => rsa.Dispose();
}
public interface ICatalogQuoteClient { Task<QuoteView> CreateAsync(CreateQuote command, CancellationToken ct); }
public sealed class QuoteConcurrencyLimit : IDisposable
{
    public SemaphoreSlim Slots { get; } = new(10, 10);
    public void Dispose() => Slots.Dispose();
}
public sealed class CatalogQuoteClient(HttpClient client, ServiceTokenIssuer issuer, QuoteConcurrencyLimit limit, TimeProvider clock) : ICatalogQuoteClient
{
    public async Task<QuoteView> CreateAsync(CreateQuote command, CancellationToken ct)
    {
        if (!await limit.Slots.WaitAsync(0, ct)) throw new ReservationProblem(503, "CatalogBusy");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/quotes") { Content = JsonContent.Create(command) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue());
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var code = problem.TryGetProperty("code", out var value) ? value.GetString() ?? "QuoteConflict" : "QuoteConflict";
                throw new ReservationProblem((int)response.StatusCode, code == "RequestIdConflict" ? "IdempotencyConflict" : code);
            }
            if (!response.IsSuccessStatusCode) throw new ReservationProblem(503, "CatalogUnavailable");
            var quote = await response.Content.ReadFromJsonAsync<QuoteView>(ct) ?? throw new ReservationProblem(503, "InvalidQuoteResponse");
            if (quote.QuoteId == Guid.Empty || quote.Session is null || quote.Session.Id != command.SessionId || quote.UnitAmountMinor != command.ExpectedUnitAmountMinor
                || quote.Currency != command.ExpectedCurrency || quote.TotalAmountMinor != checked(command.Participants * quote.UnitAmountMinor))
                throw new ReservationProblem(503, "InvalidQuoteResponse");
            if (quote.ExpiresAtUtc <= clock.GetUtcNow()) throw new ReservationProblem(409, "QuoteExpired");
            return quote;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ReservationProblem(503, "CatalogTimeout"); }
        catch (HttpRequestException) { throw new ReservationProblem(503, "CatalogUnavailable"); }
        catch (JsonException) { throw new ReservationProblem(503, "InvalidQuoteResponse"); }
        finally { limit.Slots.Release(); }
    }
}
