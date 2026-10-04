using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace Shared.Infrastructure.Messaging;

public sealed class MessagingOptions
{
    public bool Enabled { get; set; }
    public bool Local { get; set; } = true;
    public string? Endpoint { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string InputQueue { get; set; } = "";
    public string DeadLetterQueueUrl { get; set; } = "";
    public Dictionary<string, string> QueueUrls { get; set; } = [];
    public int LeaseSeconds { get; set; } = 60;
    public int VisibilitySeconds { get; set; } = 60;
    public int WaitSeconds { get; set; } = 20;
    public bool IsValid() => !Enabled || (LeaseSeconds >= 60 && VisibilitySeconds >= 60 && WaitSeconds is >= 0 and <= 20
        && InputQueue is "tourlab-catalog" or "tourlab-reservations" or "tourlab-payments" or "tourlab-notifications"
        && new[] { "catalog", "reservations", "payments", "notifications" }.All(s => QueueUrls.TryGetValue("tourlab-" + s, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (Local || uri.Scheme == "https"))
        && (!Local || Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "http" && endpoint.Host is "localhost" or "127.0.0.1" or "elasticmq"));
}
public sealed record ReceivedDelivery(string Queue, string ReceiptHandle, string Body);
public interface IMessageTransport
{
    Task SendAsync(string destination, string body, CancellationToken ct);
    Task<ReceivedDelivery[]> ReceiveAsync(string queue, int waitSeconds, int visibilitySeconds, CancellationToken ct);
    Task DeleteAsync(ReceivedDelivery delivery, CancellationToken ct);
    Task ExtendAsync(ReceivedDelivery delivery, int visibilitySeconds, CancellationToken ct);
}
public sealed class SqsTransport(IAmazonSQS sqs, IOptions<MessagingOptions> options) : IMessageTransport
{
    private string Url(string queue) => options.Value.QueueUrls.TryGetValue(queue, out var url) ? url : throw new PoisonMessageException("UnknownQueue");
    public async Task SendAsync(string destination, string body, CancellationToken ct) =>
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = Url(destination), MessageBody = body }, ct);
    public async Task<ReceivedDelivery[]> ReceiveAsync(string queue, int waitSeconds, int visibilitySeconds, CancellationToken ct)
    {
        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = Url(queue), MaxNumberOfMessages = 1,
            WaitTimeSeconds = waitSeconds, VisibilityTimeout = visibilitySeconds }, ct);
        return response.Messages?.Select(m => new ReceivedDelivery(queue, m.ReceiptHandle, m.Body)).ToArray() ?? [];
    }
    public async Task DeleteAsync(ReceivedDelivery delivery, CancellationToken ct) => await sqs.DeleteMessageAsync(Url(delivery.Queue), delivery.ReceiptHandle, ct);
    public async Task ExtendAsync(ReceivedDelivery delivery, int visibilitySeconds, CancellationToken ct) => await sqs.ChangeMessageVisibilityAsync(Url(delivery.Queue), delivery.ReceiptHandle, visibilitySeconds, ct);
}
public static class MessagingRegistration
{
    public static IServiceCollection AddLabMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<MessagingOptions>().Bind(config.GetSection("Messaging")).Validate(o => o.IsValid(), "Invalid bounded messaging configuration.").ValidateOnStart();
        services.AddSingleton<IAmazonSQS>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
            var sdk = new AmazonSQSConfig { Timeout = TimeSpan.FromSeconds(25), MaxErrorRetry = 0 };
            if (o.Local) { sdk.ServiceURL = o.Endpoint; sdk.AuthenticationRegion = o.Region; return new AmazonSQSClient(new BasicAWSCredentials("local-simulation", "local-simulation"), sdk); }
            sdk.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
            return new AmazonSQSClient(sdk); // default credential chain, including ECS task-role credentials
        });
        services.AddSingleton<IMessageTransport, SqsTransport>();
        services.AddScoped<MessagePump>(); services.AddHostedService<ConsumerWorker>(); services.AddHostedService<OutboxWorker>();
        return services;
    }
}
