using System.Diagnostics;
using System.Security.Cryptography;
using Catalog.Api.Application;
using Catalog.Api.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
namespace Catalog.Api;

public static class CatalogEndpoints
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        // Missing mutation fields must not silently become false/zero (for example deactivate a tour).
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.RespectRequiredConstructorParameters = true);
        services.AddDbContext<CatalogDb>(o => o.UseNpgsql(configuration.GetConnectionString("Runtime"),
            pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "catalog")));
        services.AddSingleton(TimeProvider.System); services.AddScoped<CatalogQueries>(); services.AddScoped<CatalogCommands>();
        services.AddScoped<QuoteHandler>(); services.AddScoped<InventoryHandlers>();
        services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("catalog-lab-admin",context=>RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value??"anonymous",_=>new FixedWindowRateLimiterOptions{PermitLimit=20,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
        var rsa = RSA.Create(); rsa.ImportFromPem(File.ReadAllText(configuration["ServiceJwt:PublicKeyPath"]
            ?? throw new InvalidOperationException("Reservations public signing key required.")));
        // Key lifetime belongs to DI, rather than a request or Blazor circuit.
        services.AddSingleton(new ReservationsVerificationKey(rsa));
        services.AddAuthentication().AddJwtBearer("Reservations", o =>
        {
            o.MapInboundClaims = false; o.IncludeErrorDetails = false;
            o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "tourlab-services",
                ValidateAudience = true, ValidAudience = "catalog-internal", ValidateLifetime = true, RequireExpirationTime = true,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new RsaSecurityKey(rsa),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.Zero };
            o.Events = new JwtBearerEvents { OnTokenValidated = context =>
            {
                var jwt = context.SecurityToken;
                if (jwt.ValidTo - jwt.ValidFrom > TimeSpan.FromMinutes(2) || jwt.ValidFrom == DateTime.MinValue)
                    context.Fail("Invalid service token lifetime.");
                return Task.CompletedTask;
            } };
        });
        services.AddAuthorization(o => o.AddPolicy("CatalogQuotes", p => p.AddAuthenticationSchemes("Reservations")
            .RequireAuthenticatedUser().RequireClaim("sub", "reservations").RequireClaim("permission", "quote:create")));
        return services;
    }
    public static void MapCatalog(this WebApplication app)
    {
        var api = app.MapGroup("/v1").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (CatalogProblem error) { return Results.Problem(statusCode: error.Status, title: error.Code,
                extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier }); }
            catch (DbUpdateConcurrencyException) { return Results.Problem(statusCode: 409, title: "VersionConflict",
                extensions: new Dictionary<string, object?> { ["code"] = "VersionConflict", ["traceId"] = context.HttpContext.TraceIdentifier }); }
        });
        var human = api.MapGroup("").RequireAuthorization("Api");
        human.MapGet("/tours", (string? search, int? page, int? pageSize, HttpContext http, CatalogQueries queries, CancellationToken ct) =>
            queries.SearchAsync(search, page ?? 1, pageSize ?? 10, http.User.IsInRole("Admin"), ct));
        human.MapGet("/tours/{id:guid}", (Guid id, HttpContext http, CatalogQueries queries, CancellationToken ct) => queries.DetailAsync(id, http.User.IsInRole("Admin"), ct));
        human.MapGet("/tours/{id:guid}/sessions", (Guid id, HttpContext http, CatalogQueries queries, CancellationToken ct) => queries.SessionsAsync(id, http.User.IsInRole("Admin"), ct));
        var admin = human.MapGroup("/admin").RequireAuthorization("AdminApi");
        admin.MapGet("/faults",async(CatalogDb db,CancellationToken ct)=>
        {if(!app.Configuration.GetValue<bool>("LabFeaturesEnabled"))throw new CatalogProblem(404,"LabFeaturesDisabled");var row=await db.Faults.AsNoTracking().SingleAsync(ct);return new FaultView(row.Mode,row.Remaining);}).RequireRateLimiting("catalog-lab-admin");
        admin.MapPut("/faults",(FaultSelection request,HttpContext http,CatalogDb db,CancellationToken ct)=>SetFaultAsync(app,request,http,db,ct)).RequireRateLimiting("catalog-lab-admin");
        admin.MapPost("/faults/reset",(HttpContext http,CatalogDb db,CancellationToken ct)=>SetFaultAsync(app,new("None",0),http,db,ct)).RequireRateLimiting("catalog-lab-admin");
        admin.MapPost("/tours", async (CreateTour command, CatalogCommands handler, CancellationToken ct) =>
        { var tour = await handler.CreateAsync(command, ct); return Results.Created($"/v1/tours/{tour.Id}", tour); });
        admin.MapPut("/tours/{id:guid}", (Guid id, UpdateTour command, CatalogCommands handler, CancellationToken ct) => handler.UpdateAsync(id, command, ct));
        admin.MapPost("/tours/{id:guid}/sessions", async (Guid id, CreateSession command, CatalogCommands handler, CancellationToken ct) =>
        { var session = await handler.CreateSessionAsync(id, command, ct); return Results.Created($"/v1/tours/{id}/sessions", session); });
        admin.MapPut("/sessions/{id:guid}/price", (Guid id, UpdatePrice command, CatalogCommands handler, CancellationToken ct) => handler.PriceAsync(id, command, ct));
        admin.MapPut("/sessions/{id:guid}/capacity", (Guid id, UpdateCapacity command, CatalogCommands handler, CancellationToken ct) => handler.CapacityAsync(id, command, ct));
        api.MapPost("/internal/quotes", (CreateQuote command, QuoteHandler handler, CancellationToken ct) => handler.HandleAsync(command, ct)).RequireAuthorization("CatalogQuotes");
    }
    private static async Task<FaultView> SetFaultAsync(WebApplication app,FaultSelection request,HttpContext http,CatalogDb db,CancellationToken ct)
    {
        if(!app.Configuration.GetValue<bool>("LabFeaturesEnabled"))throw new CatalogProblem(404,"LabFeaturesDisabled");
        if(request.Mode is not ("None" or "RejectNextConfirmation") || request.Occurrences is <0 or >100)throw new CatalogProblem(400,"InvalidFaultSelection");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var row=(await db.Faults.FromSqlRaw("SELECT * FROM catalog.\"Faults\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct)).Single();
        row.Mode=request.Mode;row.Remaining=request.Occurrences;
        db.FaultAudit.Add(new CatalogFaultAudit{Id=Guid.NewGuid(),Actor=http.User.FindFirst("sub")?.Value??"",Action=$"Simulated mode {request.Mode}; occurrences {request.Occurrences}",OccurredAtUtc=DateTimeOffset.UtcNow});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(row.Mode,row.Remaining);
    }
    private sealed class ReservationsVerificationKey(RSA rsa) : IDisposable { public void Dispose() => rsa.Dispose(); }
}
