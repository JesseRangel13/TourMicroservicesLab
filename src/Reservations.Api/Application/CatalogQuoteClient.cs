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
    public int TotalTimeoutSeconds { get; set; } = 10;
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
public sealed class QuoteConcurrencyLimit(TimeProvider clock) : IDisposable
{
    public SemaphoreSlim Slots { get; } = new(10, 10);
    private readonly object gate = new();
    private int failures;
    private long generation;
    private DateTimeOffset? openUntil;
    private bool probing;
    public long Enter()
    {
        lock (gate)
        {
            if (openUntil is not null)
            {
                if (clock.GetUtcNow() < openUntil || probing) { Shared.Infrastructure.LabTelemetry.Count("resilience", "open_rejection"); throw new ReservationProblem(503, "CatalogCircuitOpen"); }
                probing = true;
            }
            return generation;
        }
    }
    public void Complete(long version, bool? failed)
    {
        lock (gate)
        {
            if (version != generation) return;
            if (failed is null) { probing = false; return; }
            if (failed.Value && (++failures >= 5 || probing))
            {
                openUntil = clock.GetUtcNow().AddSeconds(30); probing = false; generation++;
                Shared.Infrastructure.LabTelemetry.Count("resilience", "open");
            }
            else if (!failed.Value)
            {
                var recovered = openUntil is not null;
                failures = 0; openUntil = null; probing = false;
                if (recovered) Shared.Infrastructure.LabTelemetry.Count("resilience", "closed");
            }
        }
    }
    public void Dispose() => Slots.Dispose();
}
public sealed class CatalogQuoteClient(HttpClient client, ServiceTokenIssuer issuer, QuoteConcurrencyLimit limit, TimeProvider clock,
    IOptions<CatalogClientOptions> options) : ICatalogQuoteClient
{
    public async Task<QuoteView> CreateAsync(CreateQuote command, CancellationToken ct)
    {
        if (!await limit.Slots.WaitAsync(0, ct)) { Shared.Infrastructure.LabTelemetry.Count("resilience", "concurrency_rejection"); throw new ReservationProblem(503, "CatalogBusy"); }
        long? version = null;
        bool? failed = null;
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TimeSpan.FromSeconds(options.Value.TotalTimeoutSeconds));
        try
        {
            version = limit.Enter();
            for (var attempt = 0; ; attempt++)
            {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/quotes") { Content = JsonContent.Create(command) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue());
            using var response = await client.SendAsync(request, timeout.Token);
            if ((int)response.StatusCode is 408 or 429 or 500 or 502 or 503 or 504)
                throw new HttpRequestException("TransientCatalogFailure",null,response.StatusCode);
            failed = false;
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                var code = problem.TryGetProperty("code", out var value) ? value.GetString() ?? "QuoteConflict" : "QuoteConflict";
                throw new ReservationProblem((int)response.StatusCode, code == "RequestIdConflict" ? "IdempotencyConflict" : code);
            }
            if (!response.IsSuccessStatusCode) throw new ReservationProblem(503, "CatalogUnavailable");
            var quote = await response.Content.ReadFromJsonAsync<QuoteView>(timeout.Token) ?? throw new ReservationProblem(503, "InvalidQuoteResponse");
            if (quote.QuoteId == Guid.Empty || quote.Session is null || quote.Session.Id != command.SessionId || quote.UnitAmountMinor != command.ExpectedUnitAmountMinor
                || quote.Currency != command.ExpectedCurrency || quote.TotalAmountMinor != checked(command.Participants * quote.UnitAmountMinor))
                throw new ReservationProblem(503, "InvalidQuoteResponse");
            if (quote.ExpiresAtUtc <= clock.GetUtcNow()) throw new ReservationProblem(409, "QuoteExpired");
            total.Token.ThrowIfCancellationRequested();
            return quote;
            }
            catch (Exception error) when (!total.IsCancellationRequested && (error is HttpRequestException http && IsTransient(http) || error is OperationCanceledException))
            {
                Shared.Infrastructure.LabTelemetry.Count("dependency_failure", error is OperationCanceledException ? "attempt_timeout" : "transport");
                if (attempt == 2)
                { failed = true; throw new ReservationProblem(503, error is OperationCanceledException ? "CatalogAttemptTimeout" : "CatalogUnavailable"); }
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt) + Random.Shared.Next(25, 101)), total.Token);
            }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failed = true; Shared.Infrastructure.LabTelemetry.Count("dependency_failure", "total_timeout"); throw new ReservationProblem(503, "CatalogTotalTimeout"); }
        catch (HttpRequestException)
        {
            ct.ThrowIfCancellationRequested();
            if(total.IsCancellationRequested){failed=true;throw new ReservationProblem(503,"CatalogTotalTimeout");}
            failed=false;throw new ReservationProblem(503,"CatalogTransportRejected");
        }
        catch (JsonException) { throw new ReservationProblem(503, "InvalidQuoteResponse"); }
        finally { if (version is not null) limit.Complete(version.Value, ct.IsCancellationRequested ? null : failed); limit.Slots.Release(); }
    }
    private static bool IsTransient(HttpRequestException error)=>error.StatusCode is not null
        ? (int)error.StatusCode is 408 or 429 or 500 or 502 or 503 or 504
        : error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded;
}
