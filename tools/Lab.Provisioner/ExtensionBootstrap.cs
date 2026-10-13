using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.S3.Model;
using Catalog.Api.Images;
using Reservations.Api.Bookings;

namespace Lab.Provisioner;

public static class ExtensionBootstrap
{
    public static async Task RunAsync(CancellationToken ct)
    {
        // Hard-coded loopback emulator endpoints. This command cannot provision AWS.
        using var s3 = S3ImageStorage.Create(new ImageStorageOptions { Local = true }, "http://localhost:9000");
        try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = "tourlab-images" }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        Console.WriteLine("Local S3 bucket ready.");
        using var dynamo = DynamoBookingStore.Create(new BookingProjectionOptions { Local = true, Endpoint = "http://localhost:8031" });
        try { await dynamo.CreateTableAsync(Table("tourlab-bookings"), ct); }
        catch (ResourceInUseException) { }
        var description = await dynamo.DescribeTableAsync("tourlab-bookings", ct);
        if (description.Table.GlobalSecondaryIndexes?.SingleOrDefault()?.IndexName != "UserBookings")
            throw new InvalidOperationException("Unexpected local projection schema; data is preserved, migrate explicitly");
        Console.WriteLine("Local private bucket and booking table/index initialized without deleting data.");
    }
    public static CreateTableRequest Table(string name) => new() {
        TableName = name, BillingMode = BillingMode.PAY_PER_REQUEST,
        AttributeDefinitions = [new("pk", ScalarAttributeType.S), new("sk", ScalarAttributeType.S), new("created", ScalarAttributeType.S)],
        KeySchema = [new("pk", KeyType.HASH), new("sk", KeyType.RANGE)],
        GlobalSecondaryIndexes = [new() { IndexName = "UserBookings", KeySchema = [new("pk", KeyType.HASH), new("created", KeyType.RANGE)], Projection = new() { ProjectionType = ProjectionType.ALL } }]
    };
}
