using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Gateway.Web.Business;
using Gateway.Web.Catalog;
using Gateway.Web.Identity;
using Gateway.Web.Reservations;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace Lab.Tests;

public sealed class CircuitIsolationTests
{
    private sealed class State(string id) : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id), new(ClaimTypes.Role, "Tourist"),
             new("sessionExpires", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString())], "test"));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, string Subject)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization?.Parameter ?? throw new InvalidOperationException("Bearer required."));
            Requests.Enqueue((request.RequestUri!.AbsolutePath, jwt.Subject));
            // Response ownership echoes the received JWT, so swapped client identities cannot pass
            // merely because the aggregate counts happen to be equal.
            object response = request.RequestUri.AbsolutePath.Contains("payments", StringComparison.Ordinal)
                ? new[] { new PaymentView(Guid.Empty, Guid.Empty, jwt.Subject, 100, "MXN", "Succeeded", "Success", null, null, 0, null) }
                : request.RequestUri.AbsolutePath.Contains("catalog", StringComparison.Ordinal)
                    ? new TourPage([new(Guid.Empty, jwt.Subject, "Simulated response", true, 1)], 1, 1, 10)
                    : new ReservationPage([new(Guid.Empty, jwt.Subject, Guid.Empty, 1, Guid.Empty, 100, 100, "MXN", DateTimeOffset.UtcNow, "Confirmed", null, DateTimeOffset.UtcNow, 1)], 1, 1, 10);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response, response.GetType()) };
        }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            Assert.Null(client.DefaultRequestHeaders.Authorization);
            return client;
        }
    }
    private sealed class Component : CatalogComponent
    {
        public Task Execute(Func<CancellationToken, Task> action) => RunAsync(action);
    }

    [Fact]
    public async Task TwoClientScopesUseFreshActualIdentityOnEveryConcurrentRequestAndRejectExpiredOrSignedOutState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tourlab-jwt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var keyPath = Path.Combine(directory, "test.key");
        try
        {
            using var rsa = RSA.Create(2048); File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
            using var issuer = new JwtIssuer(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Jwt:PrivateKeyPath"] = keyPath }).Build());
            using var handler = new Handler(); var factory = new Factory(handler);
            var options = Options.Create(new GatewayApiOptions { BaseAddress = "https://localhost:8443/" });
            var alice = new State("alice-scope"); var bob = new State("bob-scope");
            var clients = new[] { alice, bob }.Select(state => (State: state,
                Catalog: new CatalogClient(factory, state, issuer, options),
                Reservations: new ReservationClient(factory, state, issuer, options),
                Business: new BusinessClient(factory, state, issuer, options))).ToArray();
            await Task.WhenAll(clients.SelectMany(scope => Enumerable.Range(0, 10).Select(async _ =>
            {
                var expected = scope.State.User.FindFirstValue(ClaimTypes.NameIdentifier);
                Assert.Equal(expected, (await scope.Catalog.SearchAsync("", 1, default)).Items.Single().Name);
                Assert.Equal(expected, (await scope.Reservations.ListAsync(1, null, null, default)).Items.Single().UserId);
                Assert.Equal(expected, (await scope.Business.PaymentsAsync(1, null, default)).Single().UserId);
            })));
            Assert.Equal(60, handler.Requests.Count);
            Assert.Equal(30, handler.Requests.Count(r => r.Subject == "alice-scope"));
            Assert.Equal(30, handler.Requests.Count(r => r.Subject == "bob-scope"));
            // Updating a scope's AuthenticationState must affect the next request, without a cached principal/token.
            alice.User = new State("alice-new-state").User;
            await clients[0].Reservations.ListAsync(1, null, null, default);
            Assert.Equal("alice-new-state", handler.Requests.Last().Subject);
            alice.User = new(new ClaimsIdentity());
            await Assert.ThrowsAsync<CatalogClientException>(() => clients[0].Catalog.SearchAsync("", 1, default));
            var identity = (ClaimsIdentity)bob.User.Identity!;
            identity.RemoveClaim(identity.FindFirst("sessionExpires")!); identity.AddClaim(new("sessionExpires", "1"));
            await Assert.ThrowsAsync<CatalogClientException>(() => clients[1].Business.PaymentsAsync(1, null, default));
            await Assert.ThrowsAsync<CatalogClientException>(() => clients[1].Reservations.ListAsync(1, null, null, default));
            Assert.Equal(61, handler.Requests.Count);
        }
        finally { File.Delete(keyPath); Directory.Delete(directory); }
    }

    [Fact]
    public async Task ComponentDisposalCancelsOutstandingIoAndRejectsQueuedActions()
    {
        var component = new Component(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var work = component.Execute(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { stopped = true; }
        });
        await started.Task; component.Dispose(); await work;
        Assert.True(stopped);
        await component.Execute(_ => throw new InvalidOperationException("Disposed components must not perform work."));
        component.Dispose();
    }
}
