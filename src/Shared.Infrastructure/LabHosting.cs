using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Shared.Contracts;

namespace Shared.Infrastructure;

public static class LabHosting
{
    public static async Task<int> CheckLivenessAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try { using var response = await client.GetAsync("https://localhost:8443/health/live"); return response.IsSuccessStatusCode ? 0 : 1; }
        catch (HttpRequestException) { return 1; }
        catch (TaskCanceledException) { return 1; }
    }
    public static void AddPrivateConfiguration(this WebApplicationBuilder builder)
    {
        var path = Environment.GetEnvironmentVariable("LAB_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("LAB_CONFIG must point to a private configuration file. Run scripts/New-LocalConfiguration.ps1.");
        builder.Configuration.AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false)
            .AddEnvironmentVariables();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
        builder.AddLabTelemetry();
        // Do not allow EF/HTTP diagnostic logs to expose private inputs.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.JwtBearer", LogLevel.Warning);
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
        {
            c.ProblemDetails.Extensions.TryAdd("code", c.ProblemDetails.Status == 500 ? "UnexpectedError" : "HttpError");
            c.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? c.HttpContext.TraceIdentifier;
        });
        builder.Services.AddExceptionHandler<SafeExceptionHandler>();
        builder.Services.AddOptions<RuntimeDatabaseOptions>().Bind(builder.Configuration.GetSection("ConnectionStrings"))
            .Validate(o => o.IsValid(), "Runtime DB requires a schema runtime role, VerifyFull TLS, and a pool size of 1..5.")
            .ValidateOnStart();
    }

    public static void AddLabJwt(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<RSA>(_ =>
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(configuration["Jwt:PublicKeyPath"]
                ?? throw new InvalidOperationException("Jwt public key path is required.")));
            return rsa;
        });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.IncludeErrorDetails = false;
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (!JwtValidation.HasRequiredIdentity(context.Principal)
                        || context.SecurityToken.ValidFrom == DateTime.MinValue
                        || context.SecurityToken.ValidTo - context.SecurityToken.ValidFrom > TimeSpan.FromMinutes(15))
                        context.Fail("Invalid human identity or token lifetime.");
                    return Task.CompletedTask;
                }
            };
        });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<RSA>((o, rsa) => o.TokenValidationParameters = JwtValidation.Create(new RsaSecurityKey(rsa)));
        services.AddAuthorization(o =>
        {
            o.AddPolicy("Api", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser());
            o.AddPolicy("AdminApi", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole("Admin"));
        });
    }

    public static void UseLabPipeline(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(async (context, next) =>
        {
            using var scope = app.Logger.BeginScope(new Dictionary<string, object?> { ["service"] = app.Environment.ApplicationName, ["event"] = "http",
                ["TraceId"] = Activity.Current?.TraceId.ToString(), ["SpanId"] = Activity.Current?.SpanId.ToString() });
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            await next(context);
            app.Logger.LogInformation("HTTP completed {StatusCode}", context.Response.StatusCode);
        });
        app.UseAuthentication();
        app.UseAuthorization();
    }

    public static void MapLabHealth(this WebApplication app)
    {
        // Liveness deliberately does not touch PostgreSQL or any dependency.
        app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" })).AllowAnonymous();
        app.MapGet("/health/ready", async (IOptions<RuntimeDatabaseOptions> options, CancellationToken ct) =>
        {
            try
            {
                await using var connection = new NpgsqlConnection(options.Value.Runtime);
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "Ready" });
            }
            catch (NpgsqlException)
            {
                return Results.Problem(statusCode: 503, title: "Database unavailable",
                    extensions: new Dictionary<string, object?> { ["code"] = "DatabaseUnavailable" });
            }
        }).RequireAuthorization("Api");
    }

    public static void MapScaffoldStatus(this WebApplication app, string service)
    {
        app.MapGet("/v1/status", (HttpContext context) =>
            new ServiceStatus(service, "LAB-007 local implementation; browser verification outstanding", context.User.FindFirst("sub")?.Value ?? ""))
            .RequireAuthorization("Api");
        app.MapGet("/v1/admin/status", () => new { service, stage = "LAB-007" }).RequireAuthorization("AdminApi");
    }
}

public static class JwtValidation
{
    // Authorization must never treat a correctly signed but incomplete identity as a user.
    // sub is opaque (Identity owns its format), unique, bounded and nonblank.
    public static bool HasRequiredIdentity(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        var subjects = principal.FindAll("sub").ToArray();
        var roles = principal.FindAll("role").ToArray();
        return subjects.Length == 1 && subjects[0].Value.Length is > 0 and <= 128
            && !string.IsNullOrWhiteSpace(subjects[0].Value)
            && subjects[0].Value == subjects[0].Value.Trim()
            && roles.Length > 0 && roles.All(c => c.Value is "Tourist" or "Admin")
            && roles.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() == roles.Length;
    }

    public static TokenValidationParameters Create(SecurityKey key) => new()
    {
        ValidateIssuer = true, ValidIssuer = "tourlab-identity",
        ValidateAudience = true, ValidAudience = "tourlab-api",
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
        ValidateIssuerSigningKey = true, IssuerSigningKey = key, RequireSignedTokens = true,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256], NameClaimType = "name", RoleClaimType = "role"
    };
}

public sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // Type and correlation are useful; exception messages may contain secrets.
        logger.LogError("Unhandled {ExceptionType}; TraceId {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        await Results.Problem(statusCode: 500, title: "An unexpected error occurred.",
            extensions: new Dictionary<string, object?> { ["code"] = "UnexpectedError", ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }
}
