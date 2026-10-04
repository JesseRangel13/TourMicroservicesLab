using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Reservations.Api.Application;
using Shared.Contracts;

namespace Lab.Tests;
public sealed class QuoteResilienceTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct); }
    private sealed class Fixture : IDisposable
    {
        private readonly string key=Path.GetTempFileName();
        public Clock Clock {get;}=new();public QuoteConcurrencyLimit Limit {get;}
        public ServiceTokenIssuer Issuer {get;}public HttpClient Http {get;}public CatalogQuoteClient Client {get;}
        public CreateQuote Command {get;}=new(Guid.NewGuid(),Guid.NewGuid(),1,"test-owner",100,"MXN");
        public Fixture(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send,int total=10)
        {
            using var rsa=RSA.Create(2048);File.WriteAllText(key,rsa.ExportRSAPrivateKeyPem());
            var options=Options.Create(new CatalogClientOptions {PrivateKeyPath=key,TotalTimeoutSeconds=total});
            Issuer=new(options);Limit=new(Clock);Http=new(new Handler(send)) {BaseAddress=new Uri("https://catalog.example/"),Timeout=Timeout.InfiniteTimeSpan};
            Client=new(Http,Issuer,Limit,Clock,options);
        }
        public HttpResponseMessage Success()=>new(HttpStatusCode.OK){Content=JsonContent.Create(new QuoteView(Guid.NewGuid(),100,100,"MXN",Clock.Now.AddMinutes(5),new SessionView(Command.SessionId,Guid.NewGuid(),Clock.Now.AddDays(1),100,"MXN",10,10,1,1)))};
        public void Dispose(){Http.Dispose();Issuer.Dispose();Limit.Dispose();File.Delete(key);}
    }
    [Fact]
    public async Task ThreeAttemptsPreserveRequestBodyAndFiveLogicalFailuresOpenOneProbeAfterThirtySeconds()
    {
        var bodies=new List<string>();bool unavailable=true;Fixture? fixture=null;
        using(fixture=new(async(request,ct)=>{bodies.Add(await request.Content!.ReadAsStringAsync(ct));return unavailable?new(HttpStatusCode.ServiceUnavailable):fixture!.Success();}))
        {
            for(var i=0;i<5;i++)Assert.Equal("CatalogUnavailable",(await Assert.ThrowsAsync<ReservationProblem>(()=>fixture.Client.CreateAsync(fixture.Command,default))).Code);
            Assert.Equal(15,bodies.Count);Assert.Single(bodies.Distinct());
            Assert.Equal("CatalogCircuitOpen",(await Assert.ThrowsAsync<ReservationProblem>(()=>fixture.Client.CreateAsync(fixture.Command,default))).Code);Assert.Equal(15,bodies.Count);
            fixture.Clock.Now=fixture.Clock.Now.AddSeconds(29);Assert.Throws<ReservationProblem>(()=>fixture.Limit.Enter());
            fixture.Clock.Now=fixture.Clock.Now.AddSeconds(1);var probe=fixture.Limit.Enter();Assert.Throws<ReservationProblem>(()=>fixture.Limit.Enter());fixture.Limit.Complete(probe,null);
            unavailable=false;await fixture.Client.CreateAsync(fixture.Command,default);Assert.Equal(16,bodies.Count);
        }
    }
    [Theory]
    [InlineData(HttpStatusCode.Conflict)][InlineData(HttpStatusCode.Unauthorized)][InlineData(HttpStatusCode.Forbidden)][InlineData(HttpStatusCode.BadRequest)]
    public async Task BusinessAndAuthenticationResponsesHaveNoRetries(HttpStatusCode code)
    {
        var count=0;using var fixture=new Fixture((_,_)=>{count++;return Task.FromResult(new HttpResponseMessage(code){Content=JsonContent.Create(new{code="PriceChanged"})});});
        await Assert.ThrowsAsync<ReservationProblem>(()=>fixture.Client.CreateAsync(fixture.Command,default));Assert.Equal(1,count);
    }
    [Fact]
    public async Task TlsAndProtocolFailuresAreNotRetried()
    {
        var count=0;using var fixture=new Fixture((_,_)=>{count++;throw new HttpRequestException(HttpRequestError.SecureConnectionError,"Certificate rejected");});
        Assert.Equal("CatalogTransportRejected",(await Assert.ThrowsAsync<ReservationProblem>(()=>fixture.Client.CreateAsync(fixture.Command,default))).Code);Assert.Equal(1,count);
    }
    [Fact]
    public async Task TenConcurrentOperationsRejectEleventhAndCallerCancellationDoesNotRetry()
    {
        var entered=0;var all=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture=new Fixture(async(_,ct)=>{if(Interlocked.Increment(ref entered)==10)all.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);});
        using var caller=new CancellationTokenSource();var tasks=Enumerable.Range(0,10).Select(_=>fixture.Client.CreateAsync(fixture.Command,caller.Token)).ToArray();
        await all.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal("CatalogBusy",(await Assert.ThrowsAsync<ReservationProblem>(()=>fixture.Client.CreateAsync(fixture.Command,default))).Code);
        await caller.CancelAsync();foreach(var task in tasks)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);Assert.Equal(10,entered);
        var version=fixture.Limit.Enter();fixture.Limit.Complete(version,false);
    }
    [Fact]
    public async Task TotalBudgetCancelsEvenFirstAttemptAndPerAttemptBudgetRetriesOnlyTwice()
    {
        var calls=0;using var fixture=new Fixture(async(_,ct)=>{calls++;await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);},1);
        var watch=System.Diagnostics.Stopwatch.StartNew();Assert.Equal("CatalogTotalTimeout",(await Assert.ThrowsAsync<ReservationProblem>(()=>fixture.Client.CreateAsync(fixture.Command,default))).Code);
        Assert.Equal(1,calls);Assert.InRange(watch.Elapsed.TotalSeconds,0.8,2.5);
        calls=0;using var attempts=new Fixture(async(_,ct)=>{calls++;await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);});watch.Restart();
        Assert.Equal("CatalogAttemptTimeout",(await Assert.ThrowsAsync<ReservationProblem>(()=>attempts.Client.CreateAsync(attempts.Command,default))).Code);Assert.Equal(3,calls);Assert.InRange(watch.Elapsed.TotalSeconds,9,10.8);
    }
    [Fact]
    public void AddingOptionalTracestateDoesNotChangeLegacyInboxFingerprint()
    {
        var original="""{"messageId":"11223344-5566-7788-9900-112233445566","deliveryId":"11223344-5566-7788-9900-112233445567","type":"TourSessionChanged","version":1,"occurredAtUtc":"2026-10-04T00:00:00+00:00","sagaId":null,"correlationId":"11223344-5566-7788-9900-112233445568","causationId":"11223344-5566-7788-9900-112233445569","traceparent":null,"payload":{}}""";
        var parsed=Shared.Infrastructure.Messaging.MessageCodec.Parse(original);
        var first=Shared.Infrastructure.Messaging.MessageCodec.Fingerprint(parsed);
        Assert.Equal(first,Shared.Infrastructure.Messaging.MessageCodec.Fingerprint(parsed with{Tracestate="lab=test"}));
        Assert.DoesNotContain("tracestate",System.Text.Json.JsonSerializer.Serialize(parsed,Shared.Infrastructure.Messaging.MessageCodec.Json));
    }
    [Fact]
    public void SuccessResetsConsecutiveFailuresAndStaleCompletionsCannotCloseNewCircuit()
    {
        var clock=new Clock();using var limit=new QuoteConcurrencyLimit(clock);
        for(var i=0;i<4;i++)limit.Complete(limit.Enter(),true);limit.Complete(limit.Enter(),false);
        var stale=limit.Enter();for(var i=0;i<5;i++)limit.Complete(limit.Enter(),true);
        limit.Complete(stale,false);Assert.Throws<ReservationProblem>(()=>limit.Enter());clock.Now=clock.Now.AddSeconds(30);
        var probe=limit.Enter();limit.Complete(probe,true);Assert.Throws<ReservationProblem>(()=>limit.Enter());
    }
}
