using Microsoft.EntityFrameworkCore;
namespace Payments.Api.Persistence;
// Owned persistence classes: no EF entity is shared with another service.
public sealed class PaymentOutbox
{
    public Guid DeliveryId {get;set;}
    public Guid MessageId {get;set;}
    public string EffectKey {get;set;} = "";
    public string Destination {get;set;} = "";
    public string Type {get;set;} = "";
    public string EnvelopeJson {get;set;} = "";
    public DateTimeOffset OccurredAtUtc {get;set;}
    public DateTimeOffset? PublishedAtUtc {get;set;}
    public Guid? LeaseOwner {get;set;}
    public DateTimeOffset? LeaseUntilUtc {get;set;}
    public int Attempts {get;set;}
}
public sealed class PaymentInbox
{
    public string ConsumerName {get;set;} = "";
    public Guid MessageId {get;set;}
    public string Fingerprint {get;set;} = "";
    public DateTimeOffset ProcessedAtUtc {get;set;}
}
public sealed class PaymentFault
{
    public int Id {get;set;} = 1;
    public string Mode {get;set;} = "Success";
    public int Remaining {get;set;}
}
public sealed class PaymentAudit
{
    public Guid Id {get;set;}
    public string Actor {get;set;} = "";
    public string Action {get;set;} = "";
    public DateTimeOffset OccurredAtUtc {get;set;}
}
public static class PaymentStorage
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<PaymentOutbox>(b=> {b.ToTable("Outbox"); b.HasKey(x=>x.DeliveryId); b.HasIndex(x=>new{x.Destination,x.EffectKey}).IsUnique(); b.Property(x=>x.EnvelopeJson).HasColumnType("jsonb"); b.HasIndex(x=>new{x.PublishedAtUtc,x.OccurredAtUtc});});
        model.Entity<PaymentInbox>(b=> {b.ToTable("Inbox"); b.HasKey(x=>new{x.ConsumerName,x.MessageId}); b.HasIndex(x=>x.ProcessedAtUtc);});
        model.Entity<PaymentFault>(b=> {b.ToTable("Faults"); b.HasKey(x=>x.Id); b.HasData(new PaymentFault());});
        model.Entity<PaymentAudit>(b=> {b.ToTable("Audit"); b.HasKey(x=>x.Id);});
    }
}
