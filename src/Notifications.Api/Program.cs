using Shared.Infrastructure.Operations;
using Shared.Infrastructure;
using Notifications.Api;
if (args.Contains("--health")) return await LabHosting.CheckLivenessAsync();
var builder = WebApplication.CreateBuilder(args);
builder.AddPrivateConfiguration();
builder.Services.AddLabJwt(builder.Configuration);
builder.Services.AddNotifications(builder.Configuration);
builder.Services.AddServiceOperations(builder.Configuration, "notifications");
var app = builder.Build();
app.UseLabPipeline();
app.UseRateLimiter();
app.MapNotifications();
app.MapLabHealth();
app.MapScaffoldStatus(app.Environment.ApplicationName);
app.MapServiceOperations("notifications");
app.Run();

return 0;
