using Shared.Infrastructure;
using Catalog.Api;
using Shared.Infrastructure.Messaging;
if (args.Contains("--health")) return await LabHosting.CheckLivenessAsync();
var builder = WebApplication.CreateBuilder(args);
builder.AddPrivateConfiguration();
builder.Services.AddLabJwt(builder.Configuration);
builder.Services.AddCatalog(builder.Configuration);
builder.Services.AddLabMessaging(builder.Configuration);
builder.Services.AddScoped<Shared.Infrastructure.Messaging.IInboxConsumer, Catalog.Api.Messaging.CatalogConsumer>();
builder.Services.AddScoped<Shared.Infrastructure.Messaging.IOutboxStore>(sp => new Shared.Infrastructure.Messaging.PgOutboxStore(
    builder.Configuration.GetConnectionString("Runtime")!, "catalog"));
builder.Services.AddScoped<Shared.Infrastructure.Messaging.OutboxDispatcher>();
builder.Services.AddHostedService<Catalog.Api.Messaging.HoldExpirationWorker>();
var app = builder.Build();
app.UseLabPipeline();
app.UseRateLimiter();
app.MapLabHealth();
app.MapScaffoldStatus(app.Environment.ApplicationName);
app.MapCatalog();
app.Run();

return 0;
