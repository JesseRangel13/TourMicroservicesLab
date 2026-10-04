using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Application;
using Notifications.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Notifications.Api;
public sealed class NotificationProblem(int status,string code) : Exception(code) {public int Status {get;}=status;public string Code {get;}=code;}
public static class NotificationEndpoints
{
    public static IServiceCollection AddNotifications(this IServiceCollection services,IConfiguration config)
    {
        services.AddDbContextFactory<NotificationsDb>(o=>o.UseNpgsql(config.GetConnectionString("Runtime"),pg=>pg.MigrationsHistoryTable("__EFMigrationsHistory","notifications")));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<NotificationProcessor>();services.AddScoped<IInboxConsumer,NotificationConsumer>();
        services.AddScoped<INotificationSender,DurableFakeSender>();
        services.AddScoped<IOutboxStore>(_=>new PgOutboxStore(config.GetConnectionString("Runtime")!,"notifications"));services.AddScoped<OutboxDispatcher>();
        services.AddOptions<NotificationWorkOptions>().Bind(config.GetSection("NotificationWork"));
        services.AddLabMessaging(config);services.AddHostedService<NotificationWorker>();
        services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("lab-admin",context=>RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value??"anonymous",_=>new FixedWindowRateLimiterOptions{PermitLimit=20,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
        return services;
    }
    public static NotificationView View(Notification row)=>new(row.Id,row.ReservationId,row.UserId,row.Kind,row.Subject,row.Body,row.Status,row.CreatedAtUtc,row.SentAtUtc);
    public static void MapNotifications(this WebApplication app)
    {
        var root=app.MapGroup("/v1").RequireAuthorization("Api").AddEndpointFilter(async(context,next)=>
        {try{return await next(context);}catch(NotificationProblem error){return Results.Problem(statusCode:error.Status,title:error.Code,extensions:new Dictionary<string,object?>{["code"]=error.Code});}});
        root.MapGet("/notifications",async(HttpContext http,NotificationsDb db,Guid? reservationId,int? page,int? pageSize,CancellationToken ct)=>
        {
            var p=page??1;var size=pageSize??10;if(p<1 || size is <1 or >50 || (long)(p-1)*size>int.MaxValue)throw new NotificationProblem(400,"InvalidPagination");
            var query=db.Notifications.AsNoTracking().Where(r=>http.User.IsInRole("Admin") || r.UserId==UserId(http));
            if(reservationId is not null)query=query.Where(r=>r.ReservationId==reservationId);
            var total=await query.CountAsync(ct);var rows=await query.OrderByDescending(r=>r.CreatedAtUtc).ThenBy(r=>r.Id).Skip((p-1)*size).Take(size).ToArrayAsync(ct);return new NotificationPage(rows.Select(View).ToArray(),total,p,size);
        });
        root.MapGet("/notifications/{id:guid}",async(Guid id,HttpContext http,NotificationsDb db,CancellationToken ct)=>View(
            await db.Notifications.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id && (http.User.IsInRole("Admin") || r.UserId==UserId(http)),ct)
            ??throw new NotificationProblem(404,"NotFound")));
        var admin=root.MapGroup("/admin").RequireAuthorization("AdminApi").RequireRateLimiting("lab-admin");
        
        admin.MapGet("/faults",async(NotificationsDb db,CancellationToken ct)=>
        {if(!app.Configuration.GetValue<bool>("LabFeaturesEnabled"))throw new NotificationProblem(404,"LabFeaturesDisabled");var row=await db.Faults.AsNoTracking().SingleAsync(ct);return new FaultView(row.Mode,row.Remaining);});
        admin.MapPut("/faults",(FaultSelection request,HttpContext http,NotificationsDb db,CancellationToken ct)=>SetFaultAsync(app,http,db,request,ct));
        admin.MapPost("/faults/reset",(HttpContext http,NotificationsDb db,CancellationToken ct)=>SetFaultAsync(app,http,db,new("None",0),ct));
    }
    private static async Task<FaultView> SetFaultAsync(WebApplication app,HttpContext http,NotificationsDb db,FaultSelection request,CancellationToken ct)
    {
        if(!app.Configuration.GetValue<bool>("LabFeaturesEnabled"))throw new NotificationProblem(404,"LabFeaturesDisabled");
        if(request.Mode is not ("None" or "FailureThenSuccess") || request.Occurrences is <0 or >100)throw new NotificationProblem(400,"InvalidFaultSelection");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var row=(await db.Faults.FromSqlRaw("SELECT * FROM notifications.\"Faults\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct)).Single();
        row.Mode=request.Mode;row.Remaining=request.Occurrences;
        db.Audit.Add(new NotificationAudit{Id=Guid.NewGuid(),Actor=UserId(http),Action=$"Simulated mode {request.Mode}; occurrences {request.Occurrences}",OccurredAtUtc=DateTimeOffset.UtcNow});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(row.Mode,row.Remaining);
    }
    private static string UserId(HttpContext http)=>http.User.FindFirst("sub")?.Value??throw new NotificationProblem(401,"InvalidIdentity");
}
