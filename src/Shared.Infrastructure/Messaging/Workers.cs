using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts;
namespace Shared.Infrastructure.Messaging;

public interface IInboxConsumer { Task ConsumeAsync(IntegrationEnvelope<JsonElement> envelope, CancellationToken ct); }
public sealed class MessagePump(IInboxConsumer consumer, IMessageTransport transport, IOptions<MessagingOptions> options)
{
    public async Task ProcessAsync(ReceivedDelivery delivery, CancellationToken ct, Func<CancellationToken, Task>? afterCommit = null)
    {
        var envelope = MessageCodec.Parse(delivery.Body);
        if (!MessageRoutes.Destinations(envelope.Type).Contains(delivery.Queue)) throw new PoisonMessageException("WrongDestination");
        using var activity = new Activity("message.consume");
        if (envelope.Traceparent is not null) activity.SetParentId(envelope.Traceparent);
        activity.Start();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(TimeSpan.FromSeconds(40));
        var renewal = RenewAsync(delivery, operation, renewalStop.Token);
        try
        {
            await consumer.ConsumeAsync(envelope, operation.Token); // returns only after local commit
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
    ILogger<ConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deliveries = await transport.ReceiveAsync(options.Value.InputQueue, options.Value.WaitSeconds, options.Value.VisibilitySeconds, stoppingToken);
                foreach (var delivery in deliveries)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<MessagePump>().ProcessAsync(delivery, stoppingToken);
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning("Message not acknowledged; {FailureType}", error.GetType().Name); }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Receive unavailable; {FailureType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
public sealed class OutboxWorker(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                sent = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(options.Value.LeaseSeconds, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning("Outbox lease retained for retry; {FailureType}", error.GetType().Name); }
            if (sent == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
