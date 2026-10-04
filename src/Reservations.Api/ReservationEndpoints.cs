using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Reservations.Api.Application;
using Reservations.Api.Messaging;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Reservations.Api;

public static class ReservationEndpoints
{
    public static IServiceCollection AddReservations(this IServiceCollection services, IConfiguration config)
    {
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.RespectRequiredConstructorParameters = true);
        services.AddDbContext<ReservationsDb>(o => o.UseNpgsql(config.GetConnectionString("Runtime"), pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")));
        services.AddOptions<CatalogClientOptions>().Bind(config.GetSection("CatalogClient"))
            .Validate(o => Uri.TryCreate(o.BaseAddress, UriKind.Absolute, out var uri) && uri.Scheme == "https" && File.Exists(o.PrivateKeyPath)
                && o.TimeoutSeconds is >= 1 and <= 10, "Catalog HTTPS, dedicated signing key, bounded timeout required.").ValidateOnStart();
        services.AddSingleton(TimeProvider.System); services.AddSingleton<ServiceTokenIssuer>(); services.AddSingleton<QuoteConcurrencyLimit>();
        services.AddHttpClient<ICatalogQuoteClient, CatalogQuoteClient>((sp, client) =>
        { var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogClientOptions>>().Value; client.BaseAddress = new Uri(o.BaseAddress); client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds); })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, MaxConnectionsPerServer = 10 });
        services.AddScoped<ReservationCreator>(); services.AddScoped<ReservationQueries>();
        services.AddOptions<SagaRecoveryOptions>().Bind(config.GetSection("SagaRecovery"))
            .Validate(o=>o.AvailabilitySeconds is >=1 and <=300 && o.ConfirmationSeconds is >=1 and <=300 && o.PaymentSeconds is >=1 and <=120 && o.RecoverySeconds is >=100 and <=600,"Bounded durable Saga deadlines required.").ValidateOnStart();
        services.AddSingleton(sp=>sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SagaRecoveryOptions>>().Value);
        services.AddScoped<SagaRecovery>();services.AddScoped<SagaCommands>();services.AddHostedService<SagaDeadlineWorker>();
        services.AddScoped<IInboxConsumer, ReservationConsumer>();
        services.AddScoped<IOutboxStore>(_ => new PgOutboxStore(config.GetConnectionString("Runtime")!, "reservations"));
        services.AddScoped<OutboxDispatcher>(); services.AddLabMessaging(config);
        return services;
    }
    public static void MapReservations(this WebApplication app)
    {
        var group = app.MapGroup("/v1/reservations").RequireAuthorization("Api").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ReservationProblem error) { return Results.Problem(statusCode: error.Status, title: error.Code, extensions:
                new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier }); }
        });
        group.MapPost("", async (HttpContext http, CreateReservation command, ReservationCreator creator, CancellationToken ct) =>
        {
            if (http.Request.Headers["Idempotency-Key"].Count != 1) throw new ReservationProblem(400, "IdempotencyKeyRequired");
            var result = await creator.CreateAsync(UserId(http), http.Request.Headers["Idempotency-Key"].ToString(), command, ct);
            http.Response.Headers.Location = result.Location;
            return Results.Content(result.ResponseBody, "application/json", statusCode: result.ResponseStatus);
        });
        group.MapGet("", (HttpContext http, ReservationQueries queries, string? userId, string? status, int? page, int? pageSize, CancellationToken ct) =>
            queries.ListAsync(UserId(http), http.User.IsInRole("Admin"), userId, status, page ?? 1, pageSize ?? 10, ct));
        group.MapGet("/{id:guid}", (Guid id, HttpContext http, ReservationQueries queries, CancellationToken ct) => queries.DetailAsync(id, UserId(http), http.User.IsInRole("Admin"), ct));
        group.MapGet("/{id:guid}/timeline", (Guid id, HttpContext http, ReservationQueries queries, CancellationToken ct) => queries.TimelineAsync(id, UserId(http), http.User.IsInRole("Admin"), ct));
        group.MapPost("/{id:guid}/cancel",async(Guid id,HttpContext http,SagaCommands commands,CancellationToken ct)=>
        {
            if(http.Request.Headers["Idempotency-Key"].Count!=1)throw new ReservationProblem(400,"IdempotencyKeyRequired");
            var result=await commands.CancelAsync(id,UserId(http),http.User.IsInRole("Admin"),http.Request.Headers["Idempotency-Key"].ToString(),ct);
            http.Response.Headers.Location=result.Location;return Results.Content(result.ResponseBody,"application/json",statusCode:result.ResponseStatus);
        });
        var admin=app.MapGroup("/v1/admin/sagas").RequireAuthorization("AdminApi").AddEndpointFilter(async(context,next)=>
        {try{return await next(context);}catch(ReservationProblem e){return Results.Problem(statusCode:e.Status,title:e.Code,extensions:new Dictionary<string,object?>{["code"]=e.Code});}});
        admin.MapGet("",async(ReservationsDb db,string? state,int? page,CancellationToken ct)=>
        {
            var p=page??1;if(p<1 || p>1000000)throw new ReservationProblem(400,"InvalidPagination");
            return (await db.Sagas.AsNoTracking().Where(x=>state==null || x.State==state).OrderBy(x=>x.Id).Skip((p-1)*20).Take(20).ToArrayAsync(ct)).Select(SagaCommands.View).ToArray();
        });
        admin.MapPost("/{id:guid}/retry-compensation",async(Guid id,HttpContext http,SagaCommands commands,CancellationToken ct)=>Results.Json(await commands.RetryAsync(id,UserId(http),ct),statusCode:202));
    }
    private static string UserId(HttpContext http) => http.User.FindFirst("sub")?.Value is { Length: > 0 and <= 128 } id ? id : throw new ReservationProblem(401, "InvalidIdentity");
}
