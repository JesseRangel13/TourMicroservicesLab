using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;
using Reservations.Api.Application;
using Reservations.Api.Persistence;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;

namespace Reservations.Api.Bookings;

public sealed class BookingProjectionOptions
{
    public bool Enabled { get; set; }
    public bool Local { get; set; } = true;
    public string Endpoint { get; set; } = "http://dynamodb:8000";
    public string Table { get; set; } = "tourlab-bookings";
    public string Region { get; set; } = "us-east-1";
    public string CursorKey { get; set; } = "";
}
public static class BookingSnapshots
{
    public static BookingSnapshotChanged From(Reservation r) => new(r.UserId, r.Id, r.TourName, r.StartsAtUtc,
        r.Participants, r.TotalAmountMinor, r.Currency, r.Status, r.Version, r.CreatedAtUtc, DateTimeOffset.UtcNow);
    public static void Validate(BookingSnapshotChanged s)
    {
        if (string.IsNullOrWhiteSpace(s.UserId) || s.UserId.Length > 128 || s.ReservationId == Guid.Empty || string.IsNullOrWhiteSpace(s.TourName) || s.TourName.Length > 200
            || s.Participants is < 1 or > 6 || s.AmountMinor <= 0 || s.Currency != "MXN" || s.Version < 1
            || s.Status is not ("AwaitingAvailability" or "AwaitingPayment" or "AwaitingConfirmation" or "Confirmed" or "Failed" or "Compensating" or "CancellationPending" or "Cancelled" or "PaymentUncertain" or "ManualReview")
            || s.StartsAtUtc == default || s.CreatedAtUtc == default || s.UpdatedAtUtc == default
            || s.StartsAtUtc.Offset != TimeSpan.Zero || s.CreatedAtUtc.Offset != TimeSpan.Zero || s.UpdatedAtUtc.Offset != TimeSpan.Zero)
            throw new PoisonMessageException("InvalidBookingSnapshot");
    }
}
public interface IBookingStore
{
    Task ApplyAsync(BookingSnapshotChanged snapshot, CancellationToken ct);
    Task<BookingSnapshotChanged?> GetAsync(string user, Guid id, CancellationToken ct);
    Task<BookingPage> ListAsync(string user, int size, string? cursor, CancellationToken ct);
}
public sealed class BookingCursor(IOptions<BookingProjectionOptions> options, TimeProvider clock)
{
    private sealed record Payload(string User, int Size, long Expires, Dictionary<string, string> Key);
    public string Encode(string user, int size, Dictionary<string, AttributeValue> key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(user, size, clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(), key.ToDictionary(x => x.Key, x => x.Value.S)));
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[payload.Length];
        using var aes = new AesGcm(Convert.FromBase64String(options.Value.CursorKey), 16); aes.Encrypt(nonce, payload, cipher, tag, "tourlab/bookings/v1"u8);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public Dictionary<string, AttributeValue>? Decode(string user, int size, string? cursor)
    {
        if (cursor is null) return null;
        try {
            if (cursor.Length is < 40 or > 4096) throw new FormatException();
            var text = cursor.Replace('-', '+').Replace('_', '/'); var bytes = Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
            var plain = new byte[bytes.Length - 28];
            using var aes = new AesGcm(Convert.FromBase64String(options.Value.CursorKey), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, "tourlab/bookings/v1"u8);
            var value = JsonSerializer.Deserialize<Payload>(plain) ?? throw new FormatException();
            if (value.User != user || value.Size != size || value.Expires <= clock.GetUtcNow().ToUnixTimeSeconds()
                || value.Key.Count != 3 || !value.Key.TryGetValue("pk", out var pk) || pk != DynamoBookingStore.UserKey(user)
                || !value.Key.ContainsKey("sk") || !value.Key.ContainsKey("created")) throw new FormatException();
            return value.Key.ToDictionary(x => x.Key, x => new AttributeValue { S = x.Value });
        } catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException) {
            throw new ReservationProblem(400, "InvalidBookingCursor");
        }
    }
}
public sealed class DynamoBookingStore(IAmazonDynamoDB client, IOptions<BookingProjectionOptions> options, BookingCursor cursors) : IBookingStore
{
    public static string UserKey(string user) => "USER#" + Convert.ToBase64String(Encoding.UTF8.GetBytes(user));
    private static Dictionary<string, AttributeValue> Key(string user, Guid id) => new() { ["pk"] = new() { S = UserKey(user) }, ["sk"] = new() { S = "BOOKING#" + id.ToString("N") } };
    public static IAmazonDynamoDB Create(BookingProjectionOptions o)
    {
        var config = new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region), MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(3) };
        if (!o.Local) return new AmazonDynamoDBClient(config);
        config.ServiceURL = o.Endpoint; config.AuthenticationRegion = o.Region;
        return new AmazonDynamoDBClient(new BasicAWSCredentials("localSimulation", "localSimulation"), config);
    }
    public async Task ApplyAsync(BookingSnapshotChanged snapshot, CancellationToken ct)
    {
        BookingSnapshots.Validate(snapshot);
        var item = Key(snapshot.UserId, snapshot.ReservationId);
        item["version"] = new() { N = snapshot.Version.ToString(CultureInfo.InvariantCulture) };
        item["created"] = new() { S = snapshot.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) + "#" + snapshot.ReservationId.ToString("N") };
        item["snapshot"] = new() { S = JsonSerializer.Serialize(snapshot, MessageCodec.Json) };
        try {
            await client.PutItemAsync(new PutItemRequest { TableName = options.Value.Table, Item = item,
                ConditionExpression = "attribute_not_exists(#v) OR #v < :v", ExpressionAttributeNames = new() { ["#v"] = "version" },
                ExpressionAttributeValues = new() { [":v"] = item["version"] } }, ct);
        } catch (ConditionalCheckFailedException) {
            var current = await GetAsync(snapshot.UserId, snapshot.ReservationId, ct);
            if (current is null || current.Version < snapshot.Version) throw new InvalidOperationException("Stale booking condition could not be verified");
        }
    }
    public async Task<BookingSnapshotChanged?> GetAsync(string user, Guid id, CancellationToken ct)
    {
        var result = await client.GetItemAsync(new GetItemRequest { TableName = options.Value.Table, Key = Key(user, id), ConsistentRead = true }, ct);
        return result.Item is { Count: > 0 } ? Read(result.Item, user) : null;
    }
    public async Task<BookingPage> ListAsync(string user, int size, string? cursor, CancellationToken ct)
    {
        if (size is < 1 or > 20) throw new ReservationProblem(400, "InvalidPagination");
        var start = cursors.Decode(user, size, cursor);
        var result = await client.QueryAsync(new QueryRequest { TableName = options.Value.Table, IndexName = "UserBookings", Limit = size,
            KeyConditionExpression = "pk = :u", ExpressionAttributeValues = new() { [":u"] = new() { S = UserKey(user) } },
            ScanIndexForward = false, ExclusiveStartKey = start }, ct);
        return new((result.Items ?? []).Select(x => Read(x, user)).ToArray(), result.LastEvaluatedKey is { Count: > 0 } ? cursors.Encode(user, size, result.LastEvaluatedKey) : null);
    }
    private static BookingSnapshotChanged Read(Dictionary<string, AttributeValue> item, string user)
    {
        var snapshot = JsonSerializer.Deserialize<BookingSnapshotChanged>(item["snapshot"].S, MessageCodec.Json) ?? throw new InvalidOperationException("Invalid projection item");
        BookingSnapshots.Validate(snapshot);
        if (snapshot.UserId != user || item["pk"].S != UserKey(user) || item["sk"].S != "BOOKING#" + snapshot.ReservationId.ToString("N")
            || item["version"].N != snapshot.Version.ToString(CultureInfo.InvariantCulture)) throw new InvalidOperationException("Projection ownership/version mismatch");
        return snapshot;
    }
}
