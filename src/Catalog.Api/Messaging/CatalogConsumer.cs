using System.Text.Json;
using Catalog.Api.Application;
using Catalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Catalog.Api.Messaging;

public sealed class CatalogConsumer(CatalogDb db, InventoryHandlers inventory) : IInboxConsumer
{
    public async Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope, CancellationToken ct)
    {
        if (envelope.Type is not ("HoldSeats" or "ConfirmSeats" or "ReleaseSeats")) throw new PoisonMessageException("CatalogHandlerNotImplemented");
        await InboxTransaction.ProcessAsync(db, "catalog", "CatalogInventory", envelope, async token =>
        {
            var context = new CatalogMessageContext(envelope.MessageId, envelope.SagaId!.Value, envelope.CorrelationId);
            try
            {
                switch (envelope.Type)
                {
                    case "HoldSeats": await inventory.HoldAsync(MessageCodec.Payload<HoldSeats>(envelope), context, token); break;
                    case "ConfirmSeats": await inventory.ConfirmAsync(MessageCodec.Payload<ConfirmSeats>(envelope), context, token); break;
                    case "ReleaseSeats": await inventory.ReleaseAsync(MessageCodec.Payload<ReleaseSeats>(envelope), context, token); break;
                }
            }
            catch (CatalogProblem error) { throw new PoisonMessageException(error.Code); }
        }, ct);
    }
}
public sealed class HoldExpirationWorker(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, ILogger<HoldExpirationWorker> logger, Shared.Infrastructure.Operations.WorkerStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.Update("hold-expiration",options.Value.Enabled,options.Value.Enabled?"Starting":"Disabled");
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid[] ids;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<CatalogDb>();
                    ids = await db.Holds.AsNoTracking().Where(h => h.Status == "Held" && h.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                        .OrderBy(h => h.ExpiresAtUtc).Take(10).Select(h => h.Id).ToArrayAsync(stoppingToken);
                }
                status.Update("hold-expiration",true,"Running");
                foreach (var id in ids)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<InventoryHandlers>().ExpireAsync(id, stoppingToken);
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested) { status.Update("hold-expiration",true,"Failure",error.GetType().Name);logger.LogWarning("Expiration deferred; {FailureType}", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
