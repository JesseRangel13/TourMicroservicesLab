namespace Shared.Contracts;
public sealed record PaymentSucceeded(Guid PaymentOperationId, string ProviderReference);
public sealed record PaymentDeclined(Guid PaymentOperationId, string Reason);
public sealed record PaymentOutcomeUnknown(Guid PaymentOperationId, DateTimeOffset NextReconcileAtUtc);
public sealed record ReconcilePayment(Guid PaymentOperationId);
public sealed record RefundPayment(Guid RefundOperationId, Guid PaymentOperationId, Guid ReservationId, string UserId, long AmountMinor, string Currency);
public sealed record ReservationConfirmed(Guid ReservationId, string UserId, string Summary);
public sealed record PaymentView(Guid Id, Guid ReservationId, string UserId, long AmountMinor, string Currency, string Status,
    string Mode, string? ProviderReference, DateTimeOffset? NextReconcileAtUtc, int ReconcileAttempts, string? Reason, bool Simulated = true);
public sealed record NotificationView(Guid Id, Guid ReservationId, string UserId, string Kind, string Subject, string Body,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? SentAtUtc, bool Simulated = true);
public sealed record NotificationPage(NotificationView[] Items, int Total, int Page, int PageSize);
public sealed record FaultSelection(string Mode, int Occurrences);
public sealed record FaultView(string Mode, int Remaining);
