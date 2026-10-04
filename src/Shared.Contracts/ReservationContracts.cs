namespace Shared.Contracts;
public sealed record CreateReservation(Guid SessionId, int Participants, long ExpectedUnitAmountMinor, string? ExpectedCurrency);
public sealed record ReservationAccepted(Guid ReservationId, string Status, Guid SagaId);
public sealed record ReservationView(Guid Id, string UserId, Guid SessionId, int Participants, Guid QuoteId,
    long UnitAmountMinor, long TotalAmountMinor, string Currency, DateTimeOffset StartsAtUtc, string Status,
    string? Reason, DateTimeOffset CreatedAtUtc, long Version);
public sealed record ReservationPage(ReservationView[] Items, int Total, int Page, int PageSize);
public sealed record TransitionView(string From, string To, string? Reason, DateTimeOffset OccurredAtUtc, Guid MessageId);
public sealed record ProcessPayment(Guid PaymentOperationId, Guid ReservationId, string UserId, long AmountMinor, string Currency);
public sealed record ReservationFailed(Guid ReservationId, string UserId, string Reason);
