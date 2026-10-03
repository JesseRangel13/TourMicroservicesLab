using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Catalog.Api.Persistence;

public sealed class Tour
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
    public long Version { get; set; } = 1;
}
public sealed class TourSession
{
    public Guid Id { get; set; }
    public Guid TourId { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public long UnitAmountMinor { get; set; }
    public string Currency { get; set; } = "MXN";
    public int Capacity { get; set; }
    public int AvailableSeats { get; set; }
    public long PriceVersion { get; set; } = 1;
    public long Version { get; set; } = 1;
}
public sealed class Quote
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid SessionId { get; set; }
    public int Participants { get; set; }
    public string UserId { get; set; } = "";
    public long UnitAmountMinor { get; set; }
    public long TotalAmountMinor { get; set; }
    public string Currency { get; set; } = "MXN";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
public sealed class SeatHold
{
    public Guid Id { get; set; }
    public Guid SagaId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? QuoteId { get; set; }
    public string? UserId { get; set; }
    public int Participants { get; set; }
    public string Status { get; set; } = "Held";
    public string? Reason { get; set; }
    public string? RequestFingerprint { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}
// Durable intents only. LAB-003 adds leases, dispatch, and Inbox consumption.
public sealed class CatalogOutbox
{
    public Guid DeliveryId { get; set; }
    public Guid MessageId { get; set; }
    public string EffectKey { get; set; } = "";
    public string Destination { get; set; } = "tourlab-reservations";
    public string Type { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}
public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<Tour> Tours => Set<Tour>();
    public DbSet<TourSession> Sessions => Set<TourSession>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();
    public DbSet<CatalogOutbox> Outbox => Set<CatalogOutbox>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("catalog");
        model.Entity<Tour>(b =>
        {
            b.ToTable("Tours", t => t.HasCheckConstraint("CK_Tour", "length(btrim(\"Name\")) > 0 AND \"Version\" > 0"));
            b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(200); b.Property(x => x.Description).HasMaxLength(4000);
            b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.Active, x.Name, x.Id });
        });
        model.Entity<TourSession>(b =>
        {
            b.ToTable("Sessions", t => t.HasCheckConstraint("CK_Session", "\"UnitAmountMinor\" > 0 AND \"Currency\" = 'MXN' AND \"Capacity\" >= 0 AND \"AvailableSeats\" >= 0 AND \"AvailableSeats\" <= \"Capacity\" AND \"Version\" > 0 AND \"PriceVersion\" > 0"));
            b.HasKey(x => x.Id); b.HasOne<Tour>().WithMany().HasForeignKey(x => x.TourId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TourId, x.StartsAtUtc }); b.Property(x => x.Currency).HasMaxLength(3); b.Property(x => x.Version).IsConcurrencyToken();
        });
        model.Entity<Quote>(b =>
        {
            b.ToTable("Quotes", t => t.HasCheckConstraint("CK_Quote", "\"Participants\" BETWEEN 1 AND 6 AND \"UnitAmountMinor\" > 0 AND \"TotalAmountMinor\" > 0 AND \"Currency\" = 'MXN' AND length(\"UserId\") > 0 AND \"TotalAmountMinor\"::numeric = \"UnitAmountMinor\"::numeric * \"Participants\""));
            b.HasKey(x => x.Id); b.HasIndex(x => x.RequestId).IsUnique(); b.HasIndex(x => x.ExpiresAtUtc);
            b.Property(x => x.UserId).HasMaxLength(128); b.Property(x => x.Currency).HasMaxLength(3);
            b.HasOne<TourSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SeatHold>(b =>
        {
            b.ToTable("Holds", t => t.HasCheckConstraint("CK_Hold", "\"Version\" > 0 AND \"Status\" IN ('Held','Confirmed','Released','Expired','Rejected') AND ((\"SessionId\" IS NULL AND \"QuoteId\" IS NULL AND \"Participants\" = 0 AND \"Status\" IN ('Released','Rejected')) OR (\"SessionId\" IS NOT NULL AND \"QuoteId\" IS NOT NULL AND \"ReservationId\" IS NOT NULL AND \"UserId\" IS NOT NULL AND \"Participants\" BETWEEN 1 AND 6 AND \"ExpiresAtUtc\" IS NOT NULL))"));
            b.HasKey(x => x.Id); b.Property(x => x.Version).IsConcurrencyToken(); b.Property(x => x.UserId).HasMaxLength(128);
            b.Property(x => x.Status).HasMaxLength(16); b.Property(x => x.Reason).HasMaxLength(100);
            b.HasIndex(x => x.QuoteId).IsUnique(); b.HasIndex(x => new { x.Status, x.ExpiresAtUtc });
            b.HasOne<TourSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Quote>().WithMany().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CatalogOutbox>(b =>
        {
            b.ToTable("Outbox"); b.HasKey(x => x.DeliveryId); b.HasIndex(x => new { x.Destination, x.EffectKey }).IsUnique();
            b.HasIndex(x => new { x.PublishedAtUtc, x.OccurredAtUtc }); b.Property(x => x.EnvelopeJson).HasColumnType("jsonb");
            b.Property(x => x.EffectKey).HasMaxLength(200); b.Property(x => x.Type).HasMaxLength(100); b.Property(x => x.Destination).HasMaxLength(100);
        });
    }
}
public sealed class CatalogDesignFactory : IDesignTimeDbContextFactory<CatalogDb>
{
    public CatalogDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CatalogDb>()
        .UseNpgsql("Host=localhost;Database=tourlab", o => o.MigrationsHistoryTable("__EFMigrationsHistory", "catalog")).Options);
}
