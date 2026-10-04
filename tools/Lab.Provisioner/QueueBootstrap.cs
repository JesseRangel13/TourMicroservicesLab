using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Text.Json;
namespace Lab.Provisioner;

public static class QueueBootstrap
{
    public static async Task RunAsync(CancellationToken ct)
    {
        // Local-only provisioner. AWS creation/deployment is a separate task.
        using var sqs = new AmazonSQSClient(new BasicAWSCredentials("local-simulation", "local-simulation"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:9324", AuthenticationRegion = "us-east-1", MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(25) });
        foreach (var service in LocalConfiguration.Schemas.Take(4))
        {
            var dlq = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = $"tourlab-{service}-dlq" }, ct);
            var attributes = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlq.QueueUrl, AttributeNames = ["QueueArn"] }, ct);
            var queue = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = $"tourlab-{service}" }, ct);
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.QueueUrl, Attributes = new()
            {
                ["VisibilityTimeout"] = "60", ["ReceiveMessageWaitTimeSeconds"] = "20",
                ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = attributes.Attributes["QueueArn"], maxReceiveCount = 5 })
            } }, ct);
        }
        Console.WriteLine("Four local input queues/DLQs initialized without purging existing messages.");
    }
}
