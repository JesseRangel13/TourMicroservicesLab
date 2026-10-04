using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Application;
using Payments.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Payments.Api;
public sealed class PaymentProblem(int status,string code) : Exception(code) {public int Status {get;}=status;public string Code {get;}=code;}
public static class PaymentEndpoints
{
    public static IServiceCollection AddPayments(this IServiceCollection services,IConfiguration config)
    {
        services.AddDbContextFactory<PaymentsDb>(o=>o.UseNpgsql(config.GetConnectionString("Runtime"),pg=>pg.MigrationsHistoryTable("__EFMigrationsHistory","payments")));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PaymentProcessor>();services.AddScoped<IInboxConsumer,PaymentConsumer>();
        services.AddScoped<IPaymentProvider,DurableFakeProvider>();
        services.AddScoped<IOutboxStore>(_=>new PgOutboxStore(config.GetConnectionString("Runtime")!,"payments"));services.AddScoped<OutboxDispatcher>();
        services.AddOptions<PaymentWorkOptions>().Bind(config.GetSection("PaymentWork"));
        services.AddLabMessaging(config);services.AddHostedService<PaymentWorker>();
        services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("lab-admin",context=>RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value??"anonymous",_=>new FixedWindowRateLimiterOptions{PermitLimit=20,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
        return services;
    }
    public static PaymentView View(PaymentOperation row)=>new(row.Id,row.ReservationId,row.UserId,row.AmountMinor,row.Currency,row.Status,row.Mode,row.ProviderReference,row.NextReconcileAtUtc,row.ReconcileAttempts,row.Reason);
    public static void MapPayments(this WebApplication app)
    {
        var root=app.MapGroup("/v1").RequireAuthorization("Api").AddEndpointFilter(async(context,next)=>
        {try{return await next(context);}catch(PaymentProblem error){return Results.Problem(statusCode:error.Status,title:error.Code,extensions:new Dictionary<string,object?>{["code"]=error.Code});}});
        root.MapGet("/payments",async(HttpContext http,PaymentsDb db,Guid? reservationId,int? page,int? pageSize,CancellationToken ct)=>
        {
            var p=page??1;var size=pageSize??10;if(p<1 || size is <1 or >50 || (long)(p-1)*size>int.MaxValue)throw new PaymentProblem(400,"InvalidPagination");
            var query=db.Operations.AsNoTracking().Where(r=>http.User.IsInRole("Admin") || r.UserId==UserId(http));
            if(reservationId is not null)query=query.Where(r=>r.ReservationId==reservationId);
            return (await query.OrderBy(r=>r.Id).Skip((p-1)*size).Take(size).ToArrayAsync(ct)).Select(View).ToArray();
        });
        root.MapGet("/payments/{id:guid}",async(Guid id,HttpContext http,PaymentsDb db,CancellationToken ct)=>View(
            await db.Operations.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id && (http.User.IsInRole("Admin") || r.UserId==UserId(http)),ct)
            ??throw new PaymentProblem(404,"NotFound")));
        var admin=root.MapGroup("/admin").RequireAuthorization("AdminApi").RequireRateLimiting("lab-admin");
        admin.MapPost("/payments/{id:guid}/reconcile",async(Guid id,HttpContext http,PaymentsDb db,CancellationToken ct)=>
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var row=(await db.Operations.FromSqlInterpolated($"SELECT * FROM payments.\"Operations\" WHERE \"Id\"={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()??throw new PaymentProblem(404,"NotFound");
            if(row.Status!="Unknown")throw new PaymentProblem(409,"ReconciliationRequiresUnknown");
            row.NextReconcileAtUtc=DateTimeOffset.UtcNow;row.ReconcileAttempts=0;row.Version++;
            db.Audit.Add(new PaymentAudit{Id=Guid.NewGuid(),Actor=UserId(http),Action="Reconcile "+id,OccurredAtUtc=DateTimeOffset.UtcNow});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Json(View(row),statusCode:202);
        });
        admin.MapGet("/faults",async(PaymentsDb db,CancellationToken ct)=>
        {if(!app.Configuration.GetValue<bool>("LabFeaturesEnabled"))throw new PaymentProblem(404,"LabFeaturesDisabled");var row=await db.Faults.AsNoTracking().SingleAsync(ct);return new FaultView(row.Mode,row.Remaining);});
        admin.MapPut("/faults",(FaultSelection request,HttpContext http,PaymentsDb db,CancellationToken ct)=>SetFaultAsync(app,http,db,request,ct));
        admin.MapPost("/faults/reset",(HttpContext http,PaymentsDb db,CancellationToken ct)=>SetFaultAsync(app,http,db,new("Success",0),ct));
    }
    private static async Task<FaultView> SetFaultAsync(WebApplication app,HttpContext http,PaymentsDb db,FaultSelection request,CancellationToken ct)
    {
        if(!app.Configuration.GetValue<bool>("LabFeaturesEnabled"))throw new PaymentProblem(404,"LabFeaturesDisabled");
        if(request.Mode is not ("Success" or "Decline" or "TimeoutAfterCharge") || request.Occurrences is <0 or >100)throw new PaymentProblem(400,"InvalidFaultSelection");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var row=(await db.Faults.FromSqlRaw("SELECT * FROM payments.\"Faults\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct)).Single();
        row.Mode=request.Mode;row.Remaining=request.Occurrences;
        db.Audit.Add(new PaymentAudit{Id=Guid.NewGuid(),Actor=UserId(http),Action=$"Simulated mode {request.Mode}; occurrences {request.Occurrences}",OccurredAtUtc=DateTimeOffset.UtcNow});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(row.Mode,row.Remaining);
    }
    private static string UserId(HttpContext http)=>http.User.FindFirst("sub")?.Value??throw new PaymentProblem(401,"InvalidIdentity");
}
