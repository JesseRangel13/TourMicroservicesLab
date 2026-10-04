using Microsoft.Extensions.Options;
using Shared.Infrastructure.Messaging;
namespace Payments.Api.Application;
public sealed class PaymentWorkOptions { public bool Enabled {get;set;}=true; }
public sealed class PaymentWorker(IServiceScopeFactory scopes,IOptions<PaymentWorkOptions> options,ILogger<PaymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if(!options.Value.Enabled)return;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();
                using var operation=CancellationTokenSource.CreateLinkedTokenSource(stop);operation.CancelAfter(TimeSpan.FromSeconds(40));
                var processed=await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ProcessAsync(operation.Token);
                if(processed)continue;
            }
            catch(Exception error) when(!stop.IsCancellationRequested){logger.LogWarning("Durable simulated work retained; {FailureType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(1),stop);
        }
    }
}
