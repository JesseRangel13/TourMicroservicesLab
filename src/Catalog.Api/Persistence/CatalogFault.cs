namespace Catalog.Api.Persistence;
public sealed class CatalogFault
{
    public int Id { get; set; } = 1;
    public string Mode { get; set; } = "None";
    public int Remaining { get; set; }
}
public sealed class CatalogFaultAudit
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
