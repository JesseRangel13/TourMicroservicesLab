using Npgsql;
namespace Shared.Infrastructure.Messaging;

public sealed record OutboxDelivery(Guid DeliveryId, Guid MessageId, string Destination, string Body, Guid LeaseOwner);
public interface IOutboxStore
{
    Task<OutboxDelivery[]> ClaimAsync(int count, int leaseSeconds, CancellationToken ct);
    Task<bool> MarkPublishedAsync(OutboxDelivery delivery, CancellationToken ct);
}
public sealed class PgOutboxStore : IOutboxStore
{
    private readonly string connectionString;
    private readonly string schema;
    private readonly Guid? selectedDelivery;
    public PgOutboxStore(string connectionString, string schema, Guid? selectedDelivery = null)
    {
        if (schema is not ("catalog" or "reservations")) throw new ArgumentException("Invalid Outbox owner.", nameof(schema));
        this.connectionString = connectionString; this.schema = schema;
        this.selectedDelivery = selectedDelivery;
    }
    public async Task<OutboxDelivery[]> ClaimAsync(int count, int leaseSeconds, CancellationToken ct)
    {
        if (count is < 1 or > 10 || leaseSeconds < 1) throw new ArgumentOutOfRangeException(nameof(count));
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
        var owner = Guid.NewGuid();
        // A single statement commits the lease before the connection is disposed and network I/O begins.
        await using var command = new NpgsqlCommand($"""
            WITH batch AS (
                SELECT "DeliveryId" FROM {schema}."Outbox"
                WHERE "PublishedAtUtc" IS NULL AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc"<=CURRENT_TIMESTAMP)
                    AND (@selected IS NULL OR "DeliveryId"=@selected)
                ORDER BY "OccurredAtUtc","DeliveryId" FOR UPDATE SKIP LOCKED LIMIT @count
            ) UPDATE {schema}."Outbox" o SET "LeaseOwner"=@owner,
                "LeaseUntilUtc"=CURRENT_TIMESTAMP+make_interval(secs=>@seconds),"Attempts"="Attempts"+1
            FROM batch WHERE o."DeliveryId"=batch."DeliveryId"
            RETURNING o."DeliveryId",o."MessageId",o."Destination",o."EnvelopeJson"::text
            """, connection);
        command.Parameters.AddWithValue("count", count); command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("seconds", (double)leaseSeconds);
        command.Parameters.Add(new NpgsqlParameter("selected", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)selectedDelivery ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<OutboxDelivery>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), owner));
        return rows.ToArray();
    }
    public async Task<bool> MarkPublishedAsync(OutboxDelivery delivery, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand($"""
            UPDATE {schema}."Outbox" SET "PublishedAtUtc"=CURRENT_TIMESTAMP,"LeaseOwner"=NULL,"LeaseUntilUtc"=NULL
            WHERE "DeliveryId"=@id AND "LeaseOwner"=@owner AND "LeaseUntilUtc">CURRENT_TIMESTAMP AND "PublishedAtUtc" IS NULL
            """, connection);
        command.Parameters.AddWithValue("id", delivery.DeliveryId); command.Parameters.AddWithValue("owner", delivery.LeaseOwner);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
public sealed class OutboxDispatcher(IOutboxStore store, IMessageTransport transport)
{
    public async Task<int> DispatchAsync(int leaseSeconds, CancellationToken ct, Func<OutboxDelivery, CancellationToken, Task>? afterSend = null)
    {
        var rows = await store.ClaimAsync(1, leaseSeconds, ct); var sent = 0;
        foreach (var row in rows)
        {
            var envelope = MessageCodec.Parse(row.Body);
            if (envelope.MessageId != row.MessageId || envelope.DeliveryId != row.DeliveryId
                || !MessageRoutes.Destinations(envelope.Type).Contains(row.Destination)) throw new PoisonMessageException("InvalidOutboxRoute");
            await transport.SendAsync(row.Destination, row.Body, ct);
            if (afterSend is not null) await afterSend(row, ct); // controlled test barrier, never exposed through HTTP
            if (await store.MarkPublishedAsync(row, ct)) sent++;
        }
        return sent;
    }
}
