using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Notifications.Api.Persistence;
public sealed class Notification
{
    public Guid Id {get;set;}
    public Guid ReservationId {get;set;}
    public Guid SagaId {get;set;}
    public Guid SourceEventId {get;set;}
    public string UserId {get;set;} = "";
    public string Kind {get;set;} = "";
    public string Subject {get;set;} = "";
    public string Body {get;set;} = "";
    public string Mode {get;set;} = "None";
    public string Status {get;set;} = "Pending";
    public DateTimeOffset CreatedAtUtc {get;set;}
    public DateTimeOffset? SentAtUtc {get;set;}
    public DateTimeOffset NextAttemptAtUtc {get;set;}
    public Guid? LeaseOwner {get;set;}
    public DateTimeOffset? LeaseUntilUtc {get;set;}
    public long Version {get;set;} = 1;
}
public sealed class DeliveryReceipt
{
    public Guid Id {get;set;}
    public Guid SourceEventId {get;set;}
    public string Kind {get;set;} = "";
    public string RequestHash {get;set;} = "";
    public int Failures {get;set;}
    public DateTimeOffset? DeliveredAtUtc {get;set;}
}
public sealed class NotificationsDb(DbContextOptions<NotificationsDb> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<DeliveryReceipt> Receipts => Set<DeliveryReceipt>();
    public DbSet<NotificationFault> Faults => Set<NotificationFault>();
    public DbSet<NotificationAudit> Audit => Set<NotificationAudit>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("notifications");
        model.Entity<Notification>(b=> {b.ToTable("Notifications"); b.HasKey(x=>x.Id); b.HasIndex(x=>new{x.ReservationId,x.Kind}).IsUnique();
            b.HasIndex(x=>new{x.UserId,x.CreatedAtUtc}); b.HasIndex(x=>new{x.Status,x.NextAttemptAtUtc,x.LeaseUntilUtc}); b.Property(x=>x.Version).IsConcurrencyToken();});
        model.Entity<DeliveryReceipt>(b=> {b.ToTable("DeliveryReceipts"); b.HasKey(x=>x.Id); b.HasIndex(x=>new{x.SourceEventId,x.Kind}).IsUnique();});
        NotificationStorage.Configure(model);
    }
}
public sealed class NotificationsDesignFactory : IDesignTimeDbContextFactory<NotificationsDb>
{
    public NotificationsDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NotificationsDb>().UseNpgsql("Host=localhost;Database=tourlab", o=>o.MigrationsHistoryTable("__EFMigrationsHistory","notifications")).Options);
}
