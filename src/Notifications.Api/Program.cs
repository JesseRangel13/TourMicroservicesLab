using Shared.Infrastructure;
if (args.Contains("--health")) return await LabHosting.CheckLivenessAsync();
var builder = WebApplication.CreateBuilder(args);
builder.AddPrivateConfiguration();
builder.Services.AddLabJwt(builder.Configuration);
var app = builder.Build();
app.UseLabPipeline();
app.MapLabHealth();
app.MapScaffoldStatus(app.Environment.ApplicationName);
app.Run();

return 0;
