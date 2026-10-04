using Shared.Infrastructure.Operations;
using Shared.Infrastructure;
using Payments.Api;
if (args.Contains("--health")) return await LabHosting.CheckLivenessAsync();
var builder = WebApplication.CreateBuilder(args);
builder.AddPrivateConfiguration();
builder.Services.AddLabJwt(builder.Configuration);
builder.Services.AddPayments(builder.Configuration);
builder.Services.AddServiceOperations(builder.Configuration, "payments");
var app = builder.Build();
app.UseLabPipeline();
app.UseRateLimiter();
app.MapPayments();
app.MapLabHealth();
app.MapScaffoldStatus(app.Environment.ApplicationName);
app.MapServiceOperations("payments");
app.Run();

return 0;
