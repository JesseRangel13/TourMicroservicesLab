namespace Shared.Contracts;
public enum LabService { Catalog, Reservations, Payments, Notifications }
public sealed record OutboxDiagnostic(Guid DeliveryId, Guid MessageId, string Type, string Destination, int Attempts, DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc, DateTimeOffset? LeaseUntilUtc);
public sealed record InboxDiagnostic(string Consumer, Guid MessageId, DateTimeOffset ProcessedAtUtc);
public sealed record WorkerDiagnostic(string Name, bool Enabled, string Status, DateTimeOffset? LastSuccessUtc, string? LastFailureType);
public sealed record ServiceDiagnostics(string Service, long PendingOutboxCount, double OldestOutboxSeconds, Dictionary<string,long> WorkByState,
    long IgnoredDuplicates, long ApproximateDlqCount, WorkerDiagnostic[] Workers, OutboxDiagnostic[] Outbox, InboxDiagnostic[] Inbox, int Page, int PageSize);
public sealed record DeadLetterView(Guid DeliveryId, Guid? MessageId, string Type, Guid? SagaId, DateTimeOffset InspectionExpiresAtUtc, bool Replayable);
public sealed record ReplayView(Guid DeliveryId, string Outcome, bool DuplicateDeliveryPossible);
public sealed record TourProjectionView(Guid SessionId, string Name, bool Active, long PriceVersion, long UnitAmountMinor, string Currency, DateTimeOffset UpdatedAtUtc);
