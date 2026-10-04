using Microsoft.Extensions.Options;
using Shared.Infrastructure.Messaging;
namespace Notifications.Api.Application;
public sealed class NotificationWorkOptions { public bool Enabled {get;set;}=true; }
public sealed class NotificationWorker(IServiceScopeFactory scopes,IOptions<NotificationWorkOptions> options,ILogger<NotificationWorker> logger) : BackgroundService
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
                var processed=await scope.ServiceProvider.GetRequiredService<NotificationProcessor>().ProcessAsync(operation.Token);
                if(processed)continue;
            }
            catch(Exception error) when(!stop.IsCancellationRequested){logger.LogWarning("Durable simulated work retained; {FailureType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(1),stop);
        }
    }
}
