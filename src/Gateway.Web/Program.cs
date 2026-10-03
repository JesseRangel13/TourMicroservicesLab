using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Gateway.Web.Components;
using Gateway.Web.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Shared.Infrastructure;

if (args.Contains("--health")) return await LabHosting.CheckLivenessAsync();
var builder = WebApplication.CreateBuilder(args);
builder.AddPrivateConfiguration();
builder.Services.AddLabIdentity(builder.Configuration.GetConnectionString("Runtime")
    ?? throw new InvalidOperationException("Identity runtime connection is required."));
builder.Services.AddLabJwt(builder.Configuration);
builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme;
    o.DefaultChallengeScheme = Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme;
    o.DefaultSignInScheme = Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme;
});
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, LabRevalidatingAuthenticationStateProvider>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DetailedErrors = false);
builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
var protectionCertificate = X509CertificateLoader.LoadPkcs12FromFile(
    builder.Configuration["DataProtection:CertificatePath"]
    ?? throw new InvalidOperationException("Data Protection certificate is required."), null);
builder.Services.AddDataProtection().SetApplicationName("TourMicroservicesLab.Gateway")
    .PersistKeysToDbContext<IdentityDb>().ProtectKeysWithCertificate(protectionCertificate);
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("credentials", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseLabPipeline();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapLabHealth();
app.MapLabAuth();
foreach (var service in new[] { "catalog", "reservations", "payments", "notifications" })
    app.Map($"/api/{service}/v1/internal/{{**rest}}", () => Results.NotFound()).AllowAnonymous();
app.MapReverseProxy();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
return 0;

public partial class Program;
