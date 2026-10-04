using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Reservations.Api.Persistence;

public sealed class Reservation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public Guid SessionId { get; set; }
    public int Participants { get; set; }
    public Guid QuoteId { get; set; }
    public long UnitAmountMinor { get; set; }
    public long TotalAmountMinor { get; set; }
    public string Currency { get; set; } = "MXN";
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Status { get; set; } = "AwaitingAvailability";
    public string? Reason { get; set; }
    public long Version { get; set; } = 1;
}
public sealed class ReservationSaga
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public Guid HoldId { get; set; }
    public Guid PaymentOperationId { get; set; }
    public Guid RefundOperationId { get; set; }
    public string State { get; set; } = "AwaitingAvailability";
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset? HoldExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public long Version { get; set; } = 1;
}
public sealed class IdempotencyRequest
{
    public string UserId { get; set; } = "";
    public string OperationName { get; set; } = "reservation-create";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid ResourceId { get; set; }
    public int ResponseStatus { get; set; } = 202;
    public string ResponseBody { get; set; } = "";
    public string Location { get; set; } = "";
}
public sealed class TransitionHistory
{
    public Guid Id { get; set; }
    public Guid SagaId { get; set; }
    public Guid MessageId { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
public sealed class ReservationOutbox
{
    public Guid DeliveryId { get; set; }
    public Guid MessageId { get; set; }
    public string EffectKey { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Type { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public int Attempts { get; set; }
}
public sealed class ReservationInbox
{
    public string ConsumerName { get; set; } = "";
    public Guid MessageId { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset ProcessedAtUtc { get; set; }
}
public sealed class TourProjection
{
    public Guid SessionId { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public long PriceVersion { get; set; }
    public long UnitAmountMinor { get; set; }
    public string Currency { get; set; } = "MXN";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class ReservationsDb(DbContextOptions<ReservationsDb> options) : DbContext(options)
{
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationSaga> Sagas => Set<ReservationSaga>();
    public DbSet<IdempotencyRequest> Idempotency => Set<IdempotencyRequest>();
    public DbSet<TransitionHistory> History => Set<TransitionHistory>();
    public DbSet<ReservationOutbox> Outbox => Set<ReservationOutbox>();
    public DbSet<ReservationInbox> Inbox => Set<ReservationInbox>();
    public DbSet<TourProjection> Projections => Set<TourProjection>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("reservations");
        model.Entity<Reservation>(b =>
        {
            b.ToTable("Reservations", t => t.HasCheckConstraint("CK_Reservation", "\"Participants\" BETWEEN 1 AND 6 AND \"UnitAmountMinor\" > 0 AND \"TotalAmountMinor\"::numeric = \"UnitAmountMinor\"::numeric * \"Participants\" AND \"Currency\"='MXN' AND \"Version\">0"));
            b.HasKey(x => x.Id); b.Property(x => x.UserId).HasMaxLength(128); b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Status).HasMaxLength(40); b.Property(x => x.Reason).HasMaxLength(100); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.UserId, x.CreatedAtUtc }); b.HasIndex(x => x.Status);
        });
        model.Entity<ReservationSaga>(b =>
        {
            b.ToTable("Sagas", t => t.HasCheckConstraint("CK_Saga", "\"Version\">0")); b.HasKey(x => x.Id);
            b.HasIndex(x => x.ReservationId).IsUnique(); b.HasIndex(x => x.HoldId).IsUnique(); b.HasIndex(x => x.PaymentOperationId).IsUnique(); b.HasIndex(x => x.RefundOperationId).IsUnique();
            b.HasIndex(x => new { x.State, x.DeadlineUtc }); b.Property(x => x.State).HasMaxLength(40); b.Property(x => x.FailureReason).HasMaxLength(100); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<IdempotencyRequest>(b =>
        {
            b.ToTable("IdempotencyRequests"); b.HasKey(x => new { x.UserId, x.OperationName, x.Key });
            b.Property(x => x.UserId).HasMaxLength(128); b.Property(x => x.Key).HasMaxLength(128); b.Property(x => x.OperationName).HasMaxLength(40);
            b.Property(x => x.RequestHash).HasMaxLength(64); b.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TransitionHistory>(b => { b.ToTable("History"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.SagaId, x.MessageId }).IsUnique();
            b.Property(x => x.From).HasMaxLength(40); b.Property(x => x.To).HasMaxLength(40); b.Property(x => x.Reason).HasMaxLength(100); b.HasIndex(x => new { x.SagaId, x.OccurredAtUtc });
            b.HasOne<ReservationSaga>().WithMany().HasForeignKey(x => x.SagaId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<ReservationOutbox>(b => { b.ToTable("Outbox"); b.HasKey(x => x.DeliveryId); b.HasIndex(x => new { x.Destination, x.EffectKey }).IsUnique();
            b.HasIndex(x => new { x.PublishedAtUtc, x.OccurredAtUtc }); b.Property(x => x.EnvelopeJson).HasColumnType("jsonb");
            b.Property(x => x.EffectKey).HasMaxLength(200); b.Property(x => x.Type).HasMaxLength(100); b.Property(x => x.Destination).HasMaxLength(100); });
        model.Entity<ReservationInbox>(b => { b.ToTable("Inbox"); b.HasKey(x => new { x.ConsumerName, x.MessageId });
            b.Property(x => x.ConsumerName).HasMaxLength(100); b.Property(x => x.Fingerprint).HasMaxLength(64); b.HasIndex(x => x.ProcessedAtUtc); });
        model.Entity<TourProjection>(b => { b.ToTable("TourProjections"); b.HasKey(x => x.SessionId); b.Property(x => x.Name).HasMaxLength(200); b.Property(x => x.Currency).HasMaxLength(3); });
    }
}
public sealed class ReservationsDesignFactory : IDesignTimeDbContextFactory<ReservationsDb>
{
    public ReservationsDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ReservationsDb>()
        .UseNpgsql("Host=localhost;Database=tourlab", o => o.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")).Options);
}
