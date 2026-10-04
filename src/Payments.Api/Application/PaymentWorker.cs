using Shared.Infrastructure.Operations;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Messaging;
namespace Payments.Api.Application;
public sealed class PaymentWorkOptions
{
    public bool Enabled {get;set;}=true;
    public int[] ReconcileIntervalsSeconds {get;set;}=[5,10,20,30,30];
}
public sealed class PaymentWorker(IServiceScopeFactory scopes,IOptions<PaymentWorkOptions> options,ILogger<PaymentWorker> logger,WorkerStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        status.Update("payment",options.Value.Enabled,options.Value.Enabled?"Starting":"Disabled");
        if(!options.Value.Enabled)return;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();
                using var operation=CancellationTokenSource.CreateLinkedTokenSource(stop);operation.CancelAfter(TimeSpan.FromSeconds(40));
                var processed=await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ProcessAsync(operation.Token);
                processed |= await scope.ServiceProvider.GetRequiredService<RefundProcessor>().ProcessAsync(operation.Token);
                status.Update("payment",true,"Running");
                if(processed)continue;
            }
            catch(Exception error) when(!stop.IsCancellationRequested){status.Update("payment",true,"Failure",error.GetType().Name);logger.LogWarning("Durable simulated work retained; {FailureType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(1),stop);
        }
    }
}
