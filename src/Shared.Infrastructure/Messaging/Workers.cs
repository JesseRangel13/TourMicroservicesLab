using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts;
using Shared.Infrastructure.Operations;
namespace Shared.Infrastructure.Messaging;

public interface IInboxConsumer { Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope, CancellationToken ct); }
public sealed class MessagePump(IInboxConsumer consumer, IMessageTransport transport, IOptions<MessagingOptions> options, ILogger<MessagePump>? logger = null)
{
    public async Task ProcessAsync(ReceivedDelivery delivery, CancellationToken ct, Func<CancellationToken, Task>? afterCommit = null)
    {
        var envelope = MessageCodec.Parse(delivery.Body);
        if (!MessageRoutes.Destinations(envelope.Type).Contains(delivery.Queue)) throw new PoisonMessageException("WrongDestination");
        ActivityContext.TryParse(envelope.Traceparent, envelope.Tracestate, true, out var parent);
        using var activity = LabTelemetry.Activities.StartActivity("message.consume", ActivityKind.Consumer, parent);
        foreach (var tag in LabTelemetry.MessageScope(envelope)) activity?.SetTag(tag.Key, tag.Value);
        using var handler = LabTelemetry.Activities.StartActivity("handler." + envelope.Type);
        var fields=LabTelemetry.MessageScope(envelope);fields["service"]=delivery.Queue;
        using var scope=logger?.BeginScope(fields);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(TimeSpan.FromSeconds(40));
        var renewal = RenewAsync(delivery, operation, renewalStop.Token);
        try
        {
            await consumer.ConsumeAsync(envelope, operation.Token); // returns only after local commit
            logger?.LogInformation("Message committed {event}",envelope.Type);
            LabTelemetry.Count("consumer", "committed");
            if (afterCommit is not null) await afterCommit(ct);
            await transport.ExtendAsync(delivery, options.Value.VisibilitySeconds, ct);
            operation.Token.ThrowIfCancellationRequested();
            await transport.DeleteAsync(delivery, ct);
        }
        finally
        {
            await renewalStop.CancelAsync();
            await renewal; // renewal is owned and observed, including during shutdown or failure
        }
    }
    private async Task RenewAsync(ReceivedDelivery delivery, CancellationTokenSource operation, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.VisibilitySeconds / 3));
            while (await timer.WaitForNextTickAsync(stop))
                await transport.ExtendAsync(delivery, options.Value.VisibilitySeconds, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { await operation.CancelAsync(); throw; }
    }
}
public sealed class ConsumerWorker(IServiceScopeFactory scopes, IMessageTransport transport, IOptions<MessagingOptions> options,
    ILogger<ConsumerWorker> logger, WorkerStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.Update("consumer",options.Value.Enabled,options.Value.Enabled?"Starting":"Disabled");
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (var control = scopes.CreateAsyncScope())
                {
                    if (await control.ServiceProvider.GetRequiredService<ServiceOperations>().TakeFaultAsync("PauseConsumption",stoppingToken))
                    { status.Update("consumer",true,"Paused");await Task.Delay(1000,stoppingToken);continue; }
                }
                var deliveries = await transport.ReceiveAsync(options.Value.InputQueue, options.Value.WaitSeconds, options.Value.VisibilitySeconds, stoppingToken);
                foreach (var delivery in deliveries)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<MessagePump>().ProcessAsync(delivery, stoppingToken);
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    { LabTelemetry.Count("consumer","failure");status.Update("consumer",true,"Failure",error.GetType().Name);logger.LogWarning("Message not acknowledged; {FailureType}", error.GetType().Name); }
                }
                status.Update("consumer",true,"Running");
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Receive unavailable; {FailureType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
public sealed class OutboxWorker(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, ILogger<OutboxWorker> logger, WorkerStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.Update("outbox",options.Value.Enabled,options.Value.Enabled?"Starting":"Disabled");
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ServiceOperations>().TakeFaultAsync("PauseOutbox",stoppingToken))
                {status.Update("outbox",true,"Paused");await Task.Delay(1000,stoppingToken);continue;}
                sent = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(options.Value.LeaseSeconds, stoppingToken);
                status.Update("outbox",true,"Running");
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            { status.Update("outbox",true,"Failure",error.GetType().Name);logger.LogWarning("Outbox lease retained for retry; {FailureType}", error.GetType().Name); }
            if (sent == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
