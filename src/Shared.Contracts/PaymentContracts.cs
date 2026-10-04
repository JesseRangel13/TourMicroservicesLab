namespace Shared.Contracts;
public sealed record PaymentSucceeded(Guid PaymentOperationId, string ProviderReference);
public sealed record PaymentDeclined(Guid PaymentOperationId, string Reason);
public sealed record PaymentOutcomeUnknown(Guid PaymentOperationId, DateTimeOffset NextReconcileAtUtc);
public sealed record ReconcilePayment(Guid PaymentOperationId);
public sealed record RefundPayment(Guid RefundOperationId, Guid PaymentOperationId, Guid ReservationId, string UserId, long AmountMinor, string Currency, long RetryVersion=0);
public sealed record PaymentRefunded(Guid RefundOperationId, Guid PaymentOperationId);
public sealed record RefundNeedsReview(Guid RefundOperationId, string Reason);
public sealed record ReservationCancelled(Guid ReservationId, string UserId, string Summary);
public sealed record ProviderResolution(string Outcome);
public sealed record RefundView(Guid Id, string Status, int Attempts, string? Reason, string? ProviderReference);
public sealed record ReservationConfirmed(Guid ReservationId, string UserId, string Summary);
public sealed record PaymentView(Guid Id, Guid ReservationId, string UserId, long AmountMinor, string Currency, string Status,
    string Mode, string? ProviderReference, DateTimeOffset? NextReconcileAtUtc, int ReconcileAttempts, string? Reason, bool Simulated = true, RefundView? Refund = null);
public sealed record NotificationView(Guid Id, Guid ReservationId, string UserId, string Kind, string Subject, string Body,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? SentAtUtc, bool Simulated = true);
public sealed record NotificationPage(NotificationView[] Items, int Total, int Page, int PageSize);
public sealed record FaultSelection(string Mode, int Occurrences);
public sealed record FaultView(string Mode, int Remaining);
