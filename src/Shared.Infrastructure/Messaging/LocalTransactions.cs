using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts;
namespace Shared.Infrastructure.Messaging;

// Infrastructure only: no shared EF entity or business table access.
public sealed class TransactionOwner : IAsyncDisposable
{
    private readonly IDbContextTransaction? owned;
    private TransactionOwner(IDbContextTransaction? transaction) => owned = transaction;
    public static async Task<TransactionOwner> BeginAsync(DbContext db, CancellationToken ct) =>
        new(db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null);
    public Task CommitAsync(CancellationToken ct) => owned?.CommitAsync(ct) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}
public static class InboxTransaction
{
    public static async Task<bool> ProcessAsync(DbContext db, string schema, string consumer,
        IntegrationEnvelope<System.Text.Json.JsonElement> envelope, Func<CancellationToken, Task> effects, CancellationToken ct)
    {
        if (schema is not ("catalog" or "reservations")) throw new ArgumentException("Unsupported owner schema.", nameof(schema));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var fingerprint = MessageCodec.Fingerprint(envelope);
        // ON CONFLICT blocks behind a concurrent uncommitted winner without aborting this transaction.
        // Only the two owner schemas above can supply an SQL identifier; all values are parameters.
        var insertSql = $$"""
            INSERT INTO {{schema}}."Inbox" ("ConsumerName","MessageId","Fingerprint","ProcessedAtUtc")
            VALUES ({0},{1},{2},CURRENT_TIMESTAMP) ON CONFLICT ("ConsumerName","MessageId") DO NOTHING
            """;
        var inserted = await db.Database.ExecuteSqlRawAsync(insertSql, [consumer, envelope.MessageId, fingerprint], ct);
        if (inserted == 0)
        {
            var duplicateSql = $"SELECT 1 AS \"Value\" FROM {schema}.\"Inbox\" WHERE \"ConsumerName\"={{0}} AND \"MessageId\"={{1}} AND \"Fingerprint\"={{2}}";
            var matches = await db.Database.SqlQueryRaw<int>(duplicateSql, consumer, envelope.MessageId, fingerprint).ToListAsync(ct);
            if (matches.Count != 1) throw new PoisonMessageException("MessageIdentityConflict");
        }
        else { await effects(ct); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct); return inserted != 0;
    }
}
