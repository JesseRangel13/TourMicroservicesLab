using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.SQS.Model;
using Amazon.SQS;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reservations.Api.Messaging;
using Shared.Contracts;
using Shared.Infrastructure;
using Shared.Infrastructure.Messaging;
using Shared.Infrastructure.Operations;

namespace Lab.Tests;
public sealed class OperationsLiveFactAttribute : FactAttribute
{ public OperationsLiveFactAttribute(){if(Environment.GetEnvironmentVariable("LAB_TEST_ROOT") is null || Environment.GetEnvironmentVariable("LAB_WORK_ENABLED")=="false")Skip="Run scripts/Verify-Operations.ps1 with live hosted workers.";} }
[Collection("Local lab")]
public sealed class OperationsTests : IDisposable
{
    private readonly HttpClient client=IntegrationTests.CreateClient();
    public OperationsTests()=>client.Timeout=TimeSpan.FromSeconds(20);
    public void Dispose()=>client.Dispose();
    // Real SDK/network for every operation except one explicitly simulated delete outage.
    private sealed class DeleteFailingSqs : AmazonSQSClient
    {
        public bool FailNextDelete;
        public DeleteFailingSqs():base(new BasicAWSCredentials("local-simulation","local-simulation"),new AmazonSQSConfig {ServiceURL="http://localhost:9324",AuthenticationRegion="us-east-1",MaxErrorRetry=0,Timeout=TimeSpan.FromSeconds(25)}){}
        public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request,CancellationToken ct=default)
        {
            if(FailNextDelete){FailNextDelete=false;throw new AmazonSQSException("SIMULATED delete transport outage");}
            return base.DeleteMessageAsync(request,ct);
        }
    }
    private sealed class CorrectableConsumer(IInboxConsumer inner) : IInboxConsumer
    {
        public bool Corrected;
        public Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope,CancellationToken ct)
        { if(!Corrected)throw new PoisonMessageException("LabConsumerConfigurationMismatch");return inner.ConsumeAsync(envelope,ct); }
    }
    private async Task<HttpResponseMessage> Send(string service,string path,HttpMethod method,string? token,object? body=null,string? key=null)
    {
        using var request=new HttpRequestMessage(method,$"https://localhost:8443/api/{service}/v1/{path}");
        if(token is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        if(body is not null)request.Content=JsonContent.Create(body,body.GetType());
        if(key is not null)request.Headers.Add("Idempotency-Key",key);
        request.Headers.Add("traceparent","00-11223344556677889900112233445566-1122334455667788-01");
        return await client.SendAsync(request);
    }
    [OperationsLiveFact]
    public async Task ServiceOwnedDiagnosticsAreBoundedRedactedAndAllOperationsRejectNonAdmins()
    {
        var alice=await ReservationTestSupport.IdentityAsync("Alice");var admin=await ReservationTestSupport.IdentityAsync("Admin");
        foreach(var service in new[]{"catalog","reservations","payments","notifications"})
        {
            foreach(var operation in new[]{("admin/diagnostics",HttpMethod.Get,(object?)null),("admin/dead-letters",HttpMethod.Get,null),($"admin/dead-letters/{Guid.NewGuid()}/replay",HttpMethod.Post,null),("admin/faults",HttpMethod.Put,(object?)new FaultSelection("None",1)),("admin/faults/reset",HttpMethod.Post,null)})
            {
                using var anonymous=await Send(service,operation.Item1,operation.Item2,null,operation.Item3);Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
                using var denied=await Send(service,operation.Item1,operation.Item2,alice.Token,operation.Item3);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            }
            using var response=await Send(service,"admin/diagnostics?pageSize=2",HttpMethod.Get,admin.Token);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);var text=await response.Content.ReadAsStringAsync();var view=JsonSerializer.Deserialize<ServiceDiagnostics>(text,MessageCodec.Json)!;
            Assert.Equal(service,view.Service);Assert.InRange(view.Outbox.Length,0,2);Assert.InRange(view.Inbox.Length,0,2);Assert.NotEmpty(view.Workers);
            foreach(var forbidden in new[]{"receiptHandle","envelopeJson","password","connectionString","requestHash","fingerprint","payload"})Assert.DoesNotContain(forbidden,text,StringComparison.OrdinalIgnoreCase);
            using var oversize=await Send(service,"admin/dead-letters?pageSize=11",HttpMethod.Get,admin.Token);Assert.Equal(HttpStatusCode.BadRequest,oversize.StatusCode);
        }
    }
    [PaymentFact]
    public async Task PoisonFiveReceivesCorrectionReplayAndSendDeleteFailureKeepOriginalIdentityAndOneProjectionEffect()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();lab.Options.InputQueue="tourlab-reservations";
        var dlq=await lab.Sqs.CreateQueueAsync(new CreateQueueRequest {QueueName="tourlab-evidence-operations-dlq-"+Guid.NewGuid().ToString("N")});
        lab.Options.DeadLetterQueueUrl=dlq.QueueUrl;
        var arn=(await lab.Sqs.GetQueueAttributesAsync(dlq.QueueUrl,["QueueArn"])).Attributes["QueueArn"];
        await lab.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest {QueueUrl=lab.Options.QueueUrls["tourlab-reservations"],Attributes=new(){["RedrivePolicy"]=JsonSerializer.Serialize(new{deadLetterTargetArn=arn,maxReceiveCount=5})}});
        var session=Guid.NewGuid();var message=Guid.NewGuid();var delivery=Guid.NewGuid();var body=MessageRoutes.Serialize(new TourSessionChanged(session,"Preserved poison recovery",true,7,700,"MXN"),message,delivery,DateTimeOffset.UtcNow,null,Guid.NewGuid(),Guid.NewGuid());
        await using var db=ReservationTestSupport.Reservations();var consumer=new CorrectableConsumer(new ReservationConsumer(db,TimeProvider.System));
        var pump=new MessagePump(consumer,lab.Transport,Options.Create(lab.Options));
        await lab.Transport.SendAsync("tourlab-reservations",body,default);
        for(var i=0;i<5;i++){var received=await lab.ReceiveAsync("tourlab-reservations");await Assert.ThrowsAsync<PoisonMessageException>(()=>pump.ProcessAsync(received,default));await lab.Transport.ExtendAsync(received,0,default);}
        _=await lab.Transport.ReceiveAsync("tourlab-reservations",0,60,default);
        using var replaySqs=new DeleteFailingSqs();
        var operations=new ServiceOperations(new("reservations",ReservationTestSupport.Connection("reservations"),true),replaySqs,Options.Create(lab.Options),new(),NullLogger<ServiceOperations>.Instance);
        DeadLetterView[] inspected=[];for(var i=0;i<20 && inspected.Length==0;i++){inspected=await operations.InspectAsync(10,"test-admin",default);if(inspected.Length==0)await Task.Delay(100);}
        var view=Assert.Single(inspected);Assert.Equal(delivery,view.DeliveryId);Assert.Equal(message,view.MessageId);Assert.True(view.Replayable);
        Assert.Empty(await operations.InspectAsync(10,"test-admin",default));
        consumer.Corrected=true;
        replaySqs.FailNextDelete=true;
        Assert.Equal("SentDeleteUncertain",(await Assert.ThrowsAsync<OperationsProblem>(()=>operations.ReplayAsync(delivery,"test-admin",default))).Code);
        var first=await lab.ReceiveAsync("tourlab-reservations");Assert.Equal(body,first.Body);await pump.ProcessAsync(first,default);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replay=operations.ReplayAsync(delivery,"test-admin",default,async ct=>{entered.SetResult();await release.Task.WaitAsync(ct);});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(409,(await Assert.ThrowsAsync<OperationsProblem>(()=>operations.ReplayAsync(delivery,"test-admin",default))).Status);release.SetResult();
        Assert.Equal("SentAndDeleteAccepted",(await replay).Outcome);
        var second=await lab.ReceiveAsync("tourlab-reservations");Assert.Equal(body,second.Body);await pump.ProcessAsync(second,default);
        Assert.Equal(1,await db.Inbox.CountAsync(x=>x.MessageId==message));Assert.Equal(1,await db.Projections.CountAsync(x=>x.SessionId==session));
        Assert.Equal(7,(await db.Projections.SingleAsync(x=>x.SessionId==session)).PriceVersion);
        Assert.Equal(409,(await Assert.ThrowsAsync<OperationsProblem>(()=>operations.ReplayAsync(delivery,"test-admin",default))).Status);
        var originalReceiptJson=JsonSerializer.Serialize(view,MessageCodec.Json);Assert.DoesNotContain("receipt",originalReceiptJson,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("700",originalReceiptJson);
        var audits=await db.Database.SqlQueryRaw<string>("SELECT \"Action\" AS \"Value\" FROM reservations.\"OperationsAudit\" WHERE \"DeliveryId\"={0}",delivery).ToListAsync();
        Assert.Contains("SentDeleteUncertain",audits);Assert.Contains("SentAndDeleteAccepted",audits);
    }
    [PaymentFact]
    public async Task PaymentPauseConfigurationDoesNotRewriteAcceptedProviderMode()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();
        var creation=await PaymentWorkflowTests.StartAsync(lab,"Decline");var payment=await PaymentWorkflowTests.PaymentId(creation.Accepted.SagaId);
        var admin=await ReservationTestSupport.IdentityAsync("Admin");
        try
        {
            using var paused=await Send("payments","admin/faults",HttpMethod.Put,admin.Token,new FaultSelection("PauseConsumption",2));Assert.Equal(HttpStatusCode.OK,paused.StatusCode);
            Assert.True(await PaymentWorkflowTests.ProcessPayment(payment));
            await using var db=PaymentWorkflowTests.Payments();var row=await db.Operations.SingleAsync(x=>x.Id==payment);Assert.Equal("Decline",row.Mode);Assert.Equal("Declined",row.Status);
            Assert.False(await db.Effects.AnyAsync(x=>x.Id==payment && x.Status=="Succeeded"));
        }
        finally {using var reset=await Send("payments","admin/faults/reset",HttpMethod.Post,admin.Token);}
    }
    [PaymentFact]
    public async Task ExpiredInspectionsRejectReplayAndCrashedReplayLeaseCanBeInspectedAgain()
    {
        await using var lab=new MessagingTests.Lab();await lab.InitAsync();lab.Options.InputQueue="tourlab-reservations";
        var queue=await lab.Sqs.CreateQueueAsync("tourlab-evidence-expiry-"+Guid.NewGuid().ToString("N"));lab.Options.DeadLetterQueueUrl=queue.QueueUrl;
        var delivery=Guid.NewGuid();var body=MessageRoutes.Serialize(new TourSessionChanged(Guid.NewGuid(),"PRIVATE-PAYLOAD-SENTINEL",true,1,100,"MXN"),Guid.NewGuid(),delivery,DateTimeOffset.UtcNow,null,Guid.NewGuid(),Guid.NewGuid());
        await lab.Sqs.SendMessageAsync(queue.QueueUrl,body);
        var operations=new ServiceOperations(new("reservations",ReservationTestSupport.Connection("reservations"),true),lab.Sqs,Options.Create(lab.Options),new(),NullLogger<ServiceOperations>.Instance);
        var inspected=Assert.Single(await operations.InspectAsync(1,"test-admin",default));Assert.DoesNotContain("PRIVATE-PAYLOAD-SENTINEL",JsonSerializer.Serialize(inspected));
        await using var db=ReservationTestSupport.Reservations();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reservations.\"DlqInspections\" SET \"ExpiresAtUtc\"=CURRENT_TIMESTAMP-interval '1 second' WHERE \"DeliveryId\"={delivery}");
        Assert.Equal(409,(await Assert.ThrowsAsync<OperationsProblem>(()=>operations.ReplayAsync(delivery,"test-admin",default))).Status);
        // Simulate a process dying after claim. Expire just this test's replay lease, retain the exact body/receipt.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reservations.\"DlqInspections\" SET \"Replaying\"=true,\"ReplayUntilUtc\"=CURRENT_TIMESTAMP-interval '1 second' WHERE \"DeliveryId\"={delivery}");
        var receipt=await db.Database.SqlQueryRaw<string>("SELECT \"Receipt\" AS \"Value\" FROM reservations.\"DlqInspections\" WHERE \"DeliveryId\"={0}",delivery).SingleAsync();
        await lab.Sqs.ChangeMessageVisibilityAsync(queue.QueueUrl,receipt,0);
        Assert.Equal(delivery,Assert.Single(await operations.InspectAsync(1,"test-admin",default)).DeliveryId);
        Assert.Equal("SentAndDeleteAccepted",(await operations.ReplayAsync(delivery,"test-admin",default)).Outcome);
        Assert.Equal(body,(await lab.ReceiveAsync("tourlab-reservations")).Body);
        var disabled=new ServiceOperations(new("reservations",ReservationTestSupport.Connection("reservations"),false),lab.Sqs,Options.Create(lab.Options),new(),NullLogger<ServiceOperations>.Instance);
        Assert.False(await disabled.TakeFaultAsync("PauseOutbox",default));
        Assert.Equal(404,(await Assert.ThrowsAsync<OperationsProblem>(()=>disabled.ReservationFaultAsync(new("PauseOutbox",1),"test-admin",default))).Status);
    }
    [PaymentFact]
    public async Task ProjectionIsAbsentBeforeInitialEventThenNewestWinsAndMessageSpansFollowIncomingContext()
    {
        var session=await ReservationTestSupport.SessionAsync();await using var lab=new MessagingTests.Lab();await lab.InitAsync();
        await using(var db=ReservationTestSupport.Reservations())Assert.False(await db.Projections.AnyAsync(x=>x.SessionId==session.Id));
        var spans=new List<Activity>();using var listener=new ActivityListener {ShouldListenTo=s=>s.Name==LabTelemetry.Name,Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=span=>spans.Add(span)};ActivitySource.AddActivityListener(listener);
        using var root=new Activity("LAB-006 evidence").SetIdFormat(ActivityIdFormat.W3C).Start();root.TraceStateString="lab=operations";
        Guid initial;await using(var db=ReservationTestSupport.Catalog())initial=(await db.Outbox.SingleAsync(x=>x.EffectKey==$"session/{session.Id}/1")).DeliveryId;
        await lab.DispatchAsync("catalog",initial);await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        var bodies=new List<string>();foreach(var version in new[]{3L,2L,3L})
        {
            var body=MessageRoutes.Serialize(new TourSessionChanged(session.Id,"Version "+version,true,version,version*100,"MXN"),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow,null,Guid.NewGuid(),Guid.NewGuid());bodies.Add(body);
            await lab.Transport.SendAsync("tourlab-reservations",body,default);await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        }
        await lab.Transport.SendAsync("tourlab-reservations",bodies[0],default);await lab.ReservationsAsync(await lab.ReceiveAsync("tourlab-reservations"));
        await using(var db=ReservationTestSupport.Reservations()){var row=await db.Projections.SingleAsync(x=>x.SessionId==session.Id);Assert.Equal(3,row.PriceVersion);Assert.Equal(300,row.UnitAmountMinor);}
        var incoming=MessageCodec.Parse(bodies[0]);Assert.Equal(root.Id,incoming.Traceparent);Assert.Equal("lab=operations",incoming.Tracestate);
        Assert.Contains(spans,s=>s.OperationName=="message.consume" && s.TraceId==root.TraceId && s.ParentSpanId==root.SpanId && s.TraceStateString=="lab=operations");
        var output=JsonSerializer.Serialize(spans.Select(s=>new{s.OperationName,TraceId=s.TraceId.ToString(),ParentSpanId=s.ParentSpanId.ToString()}));Assert.Contains(root.TraceId.ToString(),output);
        File.WriteAllText(Path.Combine(IntegrationTests.Root,".local","lab006-message-traces.json"),output);
        var alice=await ReservationTestSupport.IdentityAsync("Alice");var found=false;
        for(var page=1;page<=100 && !found;page++){using var response=await Send("reservations",$"projections/tour-sessions?page={page}&pageSize=50",HttpMethod.Get,alice.Token);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var rows=(await response.Content.ReadFromJsonAsync<TourProjectionView[]>())!;found=rows.Any(x=>x.SessionId==session.Id && x.PriceVersion==3);if(rows.Length<50)break;}
        Assert.True(found);
        using var rejectedProjectionPrice=await Send("reservations","reservations",HttpMethod.Post,alice.Token,new CreateReservation(session.Id,1,300,"MXN"),Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict,rejectedProjectionPrice.StatusCode);
        using var authoritativeQuote=await Send("reservations","reservations",HttpMethod.Post,alice.Token,new CreateReservation(session.Id,1,session.UnitAmountMinor,"MXN"),Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted,authoritativeQuote.StatusCode);
    }
    [OperationsLiveFact]
    public async Task SlowCatalogFiveFailuresOpenCircuitThenRecoveryCreatesWorkOnlyAfterQuoteSucceeds()
    {
        var alice=await ReservationTestSupport.IdentityAsync("Alice");var admin=await ReservationTestSupport.IdentityAsync("Admin");var session=await ReservationTestSupport.SessionAsync();
        using(var selected=await Send("catalog","admin/faults",HttpMethod.Put,admin.Token,new FaultSelection("QuoteTimeout",100)))Assert.Equal(HttpStatusCode.OK,selected.StatusCode);
        try
        {
            var command=new CreateReservation(session.Id,1,session.UnitAmountMinor,"MXN");var key=Guid.NewGuid().ToString();var watch=Stopwatch.StartNew();
            var responses=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>Send("reservations","reservations",HttpMethod.Post,alice.Token,command,Guid.NewGuid().ToString())));
            foreach(var response in responses){using(response){Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Contains("CatalogAttemptTimeout",await response.Content.ReadAsStringAsync());}}
            Assert.InRange(watch.Elapsed.TotalSeconds,9,15);
            using(var open=await Send("reservations","reservations",HttpMethod.Post,alice.Token,command,key)){Assert.Equal(HttpStatusCode.ServiceUnavailable,open.StatusCode);Assert.Contains("CatalogCircuitOpen",await open.Content.ReadAsStringAsync());}
            await using(var db=ReservationTestSupport.Reservations())Assert.False(await db.Reservations.AnyAsync(x=>x.SessionId==session.Id));
            using(var fault=await Send("catalog","admin/faults",HttpMethod.Get,admin.Token))Assert.Equal(85,(await fault.Content.ReadFromJsonAsync<FaultView>())!.Remaining);
            using(var reset=await Send("catalog","admin/faults/reset",HttpMethod.Post,admin.Token))Assert.Equal(HttpStatusCode.OK,reset.StatusCode);
            await Task.Delay(TimeSpan.FromSeconds(30));using var recovered=await Send("reservations","reservations",HttpMethod.Post,alice.Token,command,key);Assert.Equal(HttpStatusCode.Accepted,recovered.StatusCode);
        }
        finally {using var reset=await Send("catalog","admin/faults/reset",HttpMethod.Post,admin.Token);}
    }
    [OperationsLiveFact]
    public async Task PauseOutboxResetResumesSameDurableReservationAndOutboxIdentity()
    {
        var admin=await ReservationTestSupport.IdentityAsync("Admin");var alice=await ReservationTestSupport.IdentityAsync("Alice");var session=await ReservationTestSupport.SessionAsync();
        using(var set=await Send("reservations","admin/faults",HttpMethod.Put,admin.Token,new FaultSelection("PauseOutbox",100)))Assert.Equal(HttpStatusCode.OK,set.StatusCode);
        try
        {
            var paused=false;for(var i=0;i<20 && !paused;i++){using var diagnostic=await Send("reservations","admin/diagnostics",HttpMethod.Get,admin.Token);var view=await diagnostic.Content.ReadFromJsonAsync<ServiceDiagnostics>();paused=view!.Workers.Any(x=>x.Name=="outbox" && x.Status=="Paused");if(!paused)await Task.Delay(100);}
            Assert.True(paused);
            using var created=await Send("reservations","reservations",HttpMethod.Post,alice.Token,new CreateReservation(session.Id,1,session.UnitAmountMinor,"MXN"),Guid.NewGuid().ToString());Assert.Equal(HttpStatusCode.Accepted,created.StatusCode);var accepted=(await created.Content.ReadFromJsonAsync<ReservationAccepted>())!;
            Guid delivery;Guid message;await using(var db=ReservationTestSupport.Reservations()){var row=await db.Outbox.SingleAsync(x=>x.EffectKey==$"saga/{accepted.SagaId}/hold");Assert.Null(row.PublishedAtUtc);delivery=row.DeliveryId;message=row.MessageId;}
            using(var reset=await Send("reservations","admin/faults/reset",HttpMethod.Post,admin.Token))Assert.Equal(HttpStatusCode.OK,reset.StatusCode);
            var published=false;for(var i=0;i<50 && !published;i++){await using var db=ReservationTestSupport.Reservations();published=await db.Outbox.AnyAsync(x=>x.DeliveryId==delivery && x.MessageId==message && x.PublishedAtUtc!=null);if(!published)await Task.Delay(200);}Assert.True(published);
        }
        finally {using var reset=await Send("reservations","admin/faults/reset",HttpMethod.Post,admin.Token);}
    }
}
