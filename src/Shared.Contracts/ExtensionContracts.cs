namespace Shared.Contracts;

public sealed record TourImageView(Guid Id, Guid TourId, string ContentType, long ByteSize, string Status, long Version, string? Url);
public sealed record BookingSnapshotChanged(string UserId, Guid ReservationId, string TourName, DateTimeOffset StartsAtUtc,
    int Participants, long AmountMinor, string Currency, string Status, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record BookingPage(BookingSnapshotChanged[] Items, string? Cursor);
public sealed record RebuildBookings(Guid? AfterId, int BatchSize = 20);
public sealed record RebuildBookingsResult(int Published, Guid? AfterId);
