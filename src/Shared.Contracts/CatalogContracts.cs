namespace Shared.Contracts;

public sealed record TourView(Guid Id, string Name, string Description, bool Active, long Version);
public sealed record SessionView(Guid Id, Guid TourId, DateTimeOffset StartsAtUtc, long UnitAmountMinor,
    string Currency, int Capacity, int AvailableSeats, long PriceVersion, long Version);
public sealed record TourPage(TourView[] Items, int Total, int Page, int PageSize);
public sealed record CreateTour(string? Name, string? Description);
public sealed record UpdateTour(string? Name, string? Description, bool Active, long ExpectedVersion);
public sealed record CreateSession(DateTimeOffset StartsAtUtc, long UnitAmountMinor, int Capacity);
public sealed record UpdatePrice(long UnitAmountMinor, long ExpectedVersion);
public sealed record UpdateCapacity(int Capacity, long ExpectedVersion);
public sealed record CreateQuote(Guid RequestId, Guid SessionId, int Participants, string? UserId,
    long ExpectedUnitAmountMinor, string? ExpectedCurrency);
public sealed record QuoteView(Guid QuoteId, long UnitAmountMinor, long TotalAmountMinor, string Currency,
    DateTimeOffset ExpiresAtUtc, SessionView Session);
public sealed record HoldSeats(Guid HoldId, Guid QuoteId, Guid ReservationId, string UserId);
public sealed record ConfirmSeats(Guid HoldId);
public sealed record ReleaseSeats(Guid HoldId, string Reason);
public sealed record SeatsHeld(Guid HoldId, DateTimeOffset ExpiresAtUtc);
public sealed record SeatsHoldRejected(Guid HoldId, string Reason);
public sealed record SeatsConfirmed(Guid HoldId);
public sealed record SeatsConfirmationRejected(Guid HoldId, string Reason);
public sealed record SeatsHoldExpired(Guid HoldId);
public sealed record SeatsReleased(Guid HoldId);
public sealed record TourSessionChanged(Guid SessionId, string TourName, bool Active, long PriceVersion,
    long UnitAmountMinor, string Currency);
public sealed record IntegrationEnvelope<T>(Guid MessageId, Guid DeliveryId, string Type, int Version,
    DateTimeOffset OccurredAtUtc, Guid? SagaId, Guid CorrelationId, Guid CausationId, string? Traceparent, T Payload);
