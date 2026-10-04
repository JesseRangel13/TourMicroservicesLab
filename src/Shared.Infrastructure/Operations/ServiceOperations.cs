using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Npgsql;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;

namespace Shared.Infrastructure.Operations;

public sealed class OperationsProblem(int status, string code) : Exception(code)
{ public int Status { get; } = status; public string Code { get; } = code; }
public sealed record OperationsOwner(string Schema, string ConnectionString, bool LabFeaturesEnabled);
// In-memory counters describe this process only; durable business counts come from the owning schema.
public sealed class WorkerStatus
{
    private readonly ConcurrentDictionary<string, WorkerDiagnostic> workers = new();
    public void Update(string name, bool enabled, string status, string? failure = null) => workers.AddOrUpdate(name,
        _ => new(name, enabled, status, status == "Running" ? DateTimeOffset.UtcNow : null, failure),
        (_, old) => old with { Enabled = enabled, Status = status, LastSuccessUtc = status == "Running" ? DateTimeOffset.UtcNow : old.LastSuccessUtc, LastFailureType = failure ?? old.LastFailureType });
    public WorkerDiagnostic[] Snapshot() => workers.Values.OrderBy(w => w.Name).ToArray();
}

public sealed class ServiceOperations(OperationsOwner owner, IAmazonSQS sqs, IOptions<MessagingOptions> messaging, WorkerStatus workers, ILogger<ServiceOperations> logger)
{
    private string Schema => owner.Schema;
    private string InputUrl => messaging.Value.QueueUrls["tourlab-" + Schema];
    private string DlqUrl => messaging.Value.DeadLetterQueueUrl;
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(owner.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private NpgsqlCommand Sql(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        return command;
    }
    public async Task<bool> TakeFaultAsync(string mode, CancellationToken ct)
    {
        if (!owner.LabFeaturesEnabled) return false;
        await using var connection = await Open(ct);
        await using var command = Sql(connection, $"UPDATE {Schema}.\"Faults\" SET \"Remaining\"=\"Remaining\"-1 WHERE \"Id\"=1 AND \"Mode\"=@mode AND \"Remaining\">0 RETURNING 1", ("mode",mode));
        var taken = await command.ExecuteScalarAsync(ct) is not null;
        if (taken) await Audit(connection, "worker", "FaultOccurrence:" + mode, null, ct);
        return taken;
    }
    public async Task<ServiceDiagnostics> DiagnosticsAsync(int page, int size, CancellationToken ct)
    {
        ValidatePage(page, size, 50);
        await using var connection = await Open(ct);
        var outbox = new List<OutboxDiagnostic>(); var inbox = new List<InboxDiagnostic>(); var states = new Dictionary<string,long>();
        long pending; double age;
        await using (var command = Sql(connection, $"SELECT count(*),COALESCE(EXTRACT(EPOCH FROM CURRENT_TIMESTAMP-min(\"OccurredAtUtc\")),0)::double precision FROM {Schema}.\"Outbox\" WHERE \"PublishedAtUtc\" IS NULL"))
        await using (var reader = await command.ExecuteReaderAsync(ct)) { await reader.ReadAsync(ct); pending=reader.GetInt64(0); age=Math.Max(0,reader.GetDouble(1)); }
        await using (var command = Sql(connection, $"SELECT \"DeliveryId\",\"MessageId\",\"Type\",\"Destination\",\"Attempts\",\"OccurredAtUtc\",\"PublishedAtUtc\",\"LeaseUntilUtc\" FROM {Schema}.\"Outbox\" ORDER BY \"OccurredAtUtc\" DESC,\"DeliveryId\" LIMIT @size OFFSET @offset",("size",size),("offset",(page-1)*size)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) outbox.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4),reader.GetFieldValue<DateTimeOffset>(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateTimeOffset>(6),reader.IsDBNull(7)?null:reader.GetFieldValue<DateTimeOffset>(7)));
        await using (var command = Sql(connection, $"SELECT \"ConsumerName\",\"MessageId\",\"ProcessedAtUtc\" FROM {Schema}.\"Inbox\" ORDER BY \"ProcessedAtUtc\" DESC,\"MessageId\" LIMIT @size OFFSET @offset",("size",size),("offset",(page-1)*size)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) inbox.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetFieldValue<DateTimeOffset>(2)));
        var queries = Schema switch
        {
            "reservations" => new[] { ("Sagas", "State", "saga") },
            "payments" => new[] { ("Operations", "Status", "payment"), ("RefundOperations", "Status", "refund") },
            "notifications" => new[] { ("Notifications", "Status", "notification") },
            _ => new[] { ("Holds", "Status", "hold") }
        };
        foreach (var (table,column,prefix) in queries)
        {
            await using var command = Sql(connection,$"SELECT \"{column}\",count(*) FROM {Schema}.\"{table}\" GROUP BY \"{column}\" LIMIT 20");
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) { states[prefix+":"+reader.GetString(0)]=reader.GetInt64(1); LabTelemetry.Measure(prefix+"."+reader.GetString(0),reader.GetInt64(1)); }
        }
        long dlq=-1;
        try
        {
            var attributes=await sqs.GetQueueAttributesAsync(DlqUrl,["ApproximateNumberOfMessages","ApproximateNumberOfMessagesNotVisible"],ct);
            dlq=attributes.Attributes.Values.Sum(v=>long.Parse(v,System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (AmazonSQSException) { LabTelemetry.Count("dependency_failure","sqs"); }
        LabTelemetry.Measure("outbox.pending",pending); LabTelemetry.Measure("outbox.age_seconds",age); LabTelemetry.Measure("dlq.approximate",dlq);
        logger.LogInformation("Diagnostics {event} {PendingOutboxCount} {OldestAgeSeconds} {ApproximateDlqCount}","snapshot",pending,age,dlq);
        return new(Schema,pending,age,states,LabTelemetry.IgnoredDuplicates,dlq,workers.Snapshot(),outbox.ToArray(),inbox.ToArray(),page,size);
    }
    public async Task<DeadLetterView[]> InspectAsync(int size,string actor,CancellationToken ct)
    {
        ValidatePage(1,size,10);
        var received=await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl=DlqUrl,MaxNumberOfMessages=size,WaitTimeSeconds=0,VisibilityTimeout=120 },ct);
        var views=new List<DeadLetterView>();
        await using var connection=await Open(ct);
        foreach(var message in received.Messages??[])
        {
            IntegrationEnvelope<JsonElement>? envelope=null;
            try { envelope=MessageCodec.Parse(message.Body); } catch(PoisonMessageException) { }
            var id=envelope?.DeliveryId??Guid.NewGuid();
            var expires=DateTimeOffset.UtcNow.AddSeconds(110);
            await using var command=Sql(connection,$"""
                INSERT INTO {Schema}."DlqInspections" ("DeliveryId","Body","Receipt","ExpiresAtUtc","Replaying","Finished")
                VALUES (@id,@body,@receipt,@expires,false,false)
                ON CONFLICT ("DeliveryId") DO UPDATE SET "Body"=@body,"Receipt"=@receipt,"ExpiresAtUtc"=@expires,"Finished"=false,"Replaying"=false,"ReplayUntilUtc"=NULL
                WHERE NOT {Schema}."DlqInspections"."Replaying" OR {Schema}."DlqInspections"."ReplayUntilUtc"<CURRENT_TIMESTAMP
                RETURNING "DeliveryId"
                """,("id",id),("body",message.Body),("receipt",message.ReceiptHandle),("expires",expires));
            if(await command.ExecuteScalarAsync(ct) is null) continue;
            views.Add(new(id,envelope?.MessageId,envelope is null?"InvalidEnvelope":KnownType(envelope.Type),envelope?.SagaId,expires,IsReplayable(envelope)));
        }
        await Audit(connection,actor,"DlqInspected",null,ct);
        return views.ToArray();
    }
    private static string KnownType(string type)
    { try { MessageRoutes.Destinations(type); return type; } catch(PoisonMessageException) { return "UnknownType"; } }
    private bool IsReplayable(IntegrationEnvelope<JsonElement>? envelope)
    { try { return envelope is not null && MessageRoutes.Destinations(envelope.Type).Contains("tourlab-"+Schema); } catch(PoisonMessageException) { return false; } }
    public async Task<ReplayView> ReplayAsync(Guid id,string actor,CancellationToken ct,Func<CancellationToken,Task>? afterSend=null)
    {
        string body; string receipt;
        await using(var connection=await Open(ct))
        {
            await using var claim=Sql(connection,$"UPDATE {Schema}.\"DlqInspections\" SET \"Replaying\"=true,\"ReplayUntilUtc\"=CURRENT_TIMESTAMP+interval '60 seconds' WHERE \"DeliveryId\"=@id AND NOT \"Finished\" AND \"ExpiresAtUtc\">CURRENT_TIMESTAMP AND (NOT \"Replaying\" OR \"ReplayUntilUtc\"<CURRENT_TIMESTAMP) RETURNING \"Body\",\"Receipt\"",("id",id));
            await using var reader=await claim.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct)) throw new OperationsProblem(409,"InspectionExpiredOrReplayInProgress");
            body=reader.GetString(0); receipt=reader.GetString(1);
            await reader.DisposeAsync();
            await Audit(connection,actor,"ReplayStarted",id,ct);
        }
        var outcome="ReplayFailed";
        try
        {
            var envelope=MessageCodec.Parse(body);
            if(!IsReplayable(envelope) || envelope.DeliveryId!=id) throw new OperationsProblem(409,"InvalidOriginalRoute");
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl=InputUrl,MessageBody=body },ct);
            outcome="SentDeleteUncertain";
            if(afterSend is not null) await afterSend(ct);
            await sqs.DeleteMessageAsync(DlqUrl,receipt,ct);
            outcome="SentAndDeleteAccepted";
            LabTelemetry.Count("dlq_replay","sent");
            return new(id,outcome,true);
        }
        catch(AmazonSQSException) { throw new OperationsProblem(503,outcome); }
        catch(PoisonMessageException) { throw new OperationsProblem(409,"InvalidOriginalEnvelope"); }
        finally
        {
            // A bounded owned cleanup token persists audit even when the caller disconnects.
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var connection=await Open(cleanup.Token);
            await using var update=Sql(connection,$"UPDATE {Schema}.\"DlqInspections\" SET \"Replaying\"=false,\"Finished\"=@finished WHERE \"DeliveryId\"=@id AND \"Receipt\"=@receipt",("finished",outcome=="SentAndDeleteAccepted"),("id",id),("receipt",receipt));
            await update.ExecuteNonQueryAsync(cleanup.Token);
            await Audit(connection,actor,outcome,id,cleanup.Token);
            logger.LogInformation("DLQ {event} {DeliveryId}",outcome,id);
        }
    }
    private async Task Audit(NpgsqlConnection connection,string actor,string action,Guid? id,CancellationToken ct)
    {
        await using var command=Sql(connection,$"INSERT INTO {Schema}.\"OperationsAudit\" (\"Id\",\"Actor\",\"Action\",\"DeliveryId\",\"OccurredAtUtc\") VALUES (@id,@actor,@action,@delivery,CURRENT_TIMESTAMP)",("id",Guid.NewGuid()),("actor",actor),("action",action),("delivery",(object?)id??DBNull.Value));
        command.Parameters["delivery"].NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid;
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<FaultView> ReservationFaultAsync(FaultSelection? selection,string actor,CancellationToken ct)
    {
        if(!owner.LabFeaturesEnabled) throw new OperationsProblem(404,"LabFeaturesDisabled");
        await using var connection=await Open(ct);
        if(selection is not null)
        {
            if(selection.Mode is not ("None" or "PauseOutbox") || selection.Occurrences is <0 or >100)throw new OperationsProblem(400,"InvalidFaultSelection");
            await using var tx=await connection.BeginTransactionAsync(ct);
            await using var update=Sql(connection,$"UPDATE {Schema}.\"Faults\" SET \"Mode\"=@mode,\"Remaining\"=@remaining WHERE \"Id\"=1",("mode",selection.Mode),("remaining",selection.Occurrences));
            await update.ExecuteNonQueryAsync(ct); await Audit(connection,actor,"Simulated:"+selection.Mode+":"+selection.Occurrences,null,ct); await tx.CommitAsync(ct);
        }
        await using var command=Sql(connection,$"SELECT \"Mode\",\"Remaining\" FROM {Schema}.\"Faults\" WHERE \"Id\"=1");
        await using var reader=await command.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);return new(reader.GetString(0),reader.GetInt32(1));
    }
    private static void ValidatePage(int page,int size,int max)
    { if(page<1 || size<1 || size>max || (long)(page-1)*size>int.MaxValue)throw new OperationsProblem(400,"InvalidPagination"); }
}

public static class OperationsRegistration
{
    public static void AddServiceOperations(this IServiceCollection services,IConfiguration config,string schema)
    {
        if(schema is not ("catalog" or "reservations" or "payments" or "notifications"))throw new ArgumentException("Invalid owner schema.",nameof(schema));
        services.AddSingleton(new OperationsOwner(schema,config.GetConnectionString("Runtime")!,config.GetValue<bool>("LabFeaturesEnabled")));
        services.AddSingleton<WorkerStatus>();services.AddScoped<ServiceOperations>();
        services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("operations-admin",http=>RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirst("sub")?.Value??"anonymous",
            _=>new FixedWindowRateLimiterOptions{PermitLimit=60,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
    }
    public static void MapServiceOperations(this WebApplication app,string schema)
    {
        var admin=app.MapGroup("/v1/admin").RequireAuthorization("AdminApi").RequireRateLimiting("operations-admin").AddEndpointFilter(async(context,next)=>
        {try{return await next(context);}catch(OperationsProblem error){return Results.Problem(statusCode:error.Status,title:error.Code,extensions:new Dictionary<string,object?>{["code"]=error.Code});}});
        admin.MapGet("/diagnostics",(ServiceOperations operations,int? page,int? pageSize,CancellationToken ct)=>operations.DiagnosticsAsync(page??1,pageSize??10,ct));
        admin.MapGet("/dead-letters",(ServiceOperations operations,HttpContext http,int? pageSize,CancellationToken ct)=>operations.InspectAsync(pageSize??10,Actor(http),ct));
        admin.MapPost("/dead-letters/{deliveryId:guid}/replay",(Guid deliveryId,ServiceOperations operations,HttpContext http,CancellationToken ct)=>operations.ReplayAsync(deliveryId,Actor(http),ct));
        if(schema=="reservations")
        {
            admin.MapGet("/faults",(ServiceOperations operations,HttpContext http,CancellationToken ct)=>operations.ReservationFaultAsync(null,Actor(http),ct));
            admin.MapPut("/faults",(FaultSelection request,ServiceOperations operations,HttpContext http,CancellationToken ct)=>operations.ReservationFaultAsync(request,Actor(http),ct));
            admin.MapPost("/faults/reset",(ServiceOperations operations,HttpContext http,CancellationToken ct)=>operations.ReservationFaultAsync(new("None",0),Actor(http),ct));
        }
    }
    private static string Actor(HttpContext http)=>http.User.FindFirst("sub")?.Value??throw new OperationsProblem(401,"InvalidIdentity");
}
