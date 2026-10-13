namespace Catalog.Api.Persistence;

public sealed class TourImage
{
    public Guid Id { get; set; }
    public Guid TourId { get; set; }
    public string ObjectKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long ByteSize { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long Version { get; set; } = 1;
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
}
