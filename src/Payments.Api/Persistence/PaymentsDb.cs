using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Payments.Api.Persistence;
public sealed class PaymentOperation
{
    public Guid Id { get; set; }
    public Guid SagaId { get; set; }
    public Guid ReservationId { get; set; }
    public string UserId { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "MXN";
    public string Status { get; set; } = "Pending";
    public string Mode { get; set; } = "Success";
    public string? ProviderReference { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? NextReconcileAtUtc { get; set; }
    public int ReconcileAttempts { get; set; }
    public Guid SourceMessageId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long Version { get; set; } = 1;
}
public sealed class ProviderEffect
{
    public Guid Id { get; set; }
    public string RequestHash { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Reference { get; set; }
    public bool TimeoutDelivered { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
public sealed class PaymentsDb(DbContextOptions<PaymentsDb> options) : DbContext(options)
{
    public DbSet<PaymentOperation> Operations => Set<PaymentOperation>();
    public DbSet<ProviderEffect> Effects => Set<ProviderEffect>();
    public DbSet<RefundOperation> Refunds => Set<RefundOperation>();
    public DbSet<ProviderRefund> ProviderRefunds => Set<ProviderRefund>();
    public DbSet<PaymentOutbox> Outbox => Set<PaymentOutbox>();
    public DbSet<PaymentFault> Faults => Set<PaymentFault>();
    public DbSet<PaymentAudit> Audit => Set<PaymentAudit>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("payments");
        model.Entity<PaymentOperation>(b => { b.ToTable("Operations", t => t.HasCheckConstraint("CK_Payment", "\"AmountMinor\">0 AND \"Currency\"='MXN' AND \"Version\">0")); b.HasKey(x=>x.Id);
            b.Property(x=>x.Version).IsConcurrencyToken(); b.Property(x=>x.UserId).HasMaxLength(128); b.HasIndex(x=>new {x.UserId,x.ReservationId});
            b.HasIndex(x=>new {x.Status,x.NextReconcileAtUtc,x.LeaseUntilUtc}); });
        model.Entity<ProviderEffect>(b=> {b.ToTable("ProviderEffects"); b.HasKey(x=>x.Id); b.Property(x=>x.RequestHash).HasMaxLength(64);});
        model.Entity<RefundOperation>(b => { b.ToTable("RefundOperations", t => t.HasCheckConstraint("CK_Refund", "\"AmountMinor\">0 AND \"Currency\"='MXN' AND \"Version\">0")); b.HasKey(x=>x.Id); b.HasIndex(x=>x.PaymentOperationId).IsUnique(); b.Property(x=>x.Version).IsConcurrencyToken(); b.HasIndex(x=>new {x.Status,x.NextAttemptAtUtc}); });
        model.Entity<ProviderRefund>(b => { b.ToTable("ProviderRefunds"); b.HasKey(x=>x.Id); b.HasIndex(x=>x.PaymentOperationId).IsUnique(); });
        PaymentStorage.Configure(model);
    }
}
public sealed class PaymentsDesignFactory : IDesignTimeDbContextFactory<PaymentsDb>
{
    public PaymentsDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PaymentsDb>().UseNpgsql("Host=localhost;Database=tourlab", o=>o.MigrationsHistoryTable("__EFMigrationsHistory","payments")).Options);
}
