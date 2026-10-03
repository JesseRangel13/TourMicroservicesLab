# 01 — Product and Business Rules

## Users and capabilities
Seed two tourists, Alice and Bob, and one Admin. Initial passwords come from private configuration, never literals in the repository. Identity stores password hashes. No public registration or password-recovery workflow.

Tourist: sign in and out; list and filter tours; inspect dates, prices, and capacity; request a reservation; view their reservations and progress; cancel a confirmed reservation before departure; inspect their simulated notifications.
Admin: all views with filters; create/edit tours; create tour sessions; adjust capacity; change prices; activate/deactivate tours; inspect Sagas, payments, Outbox, Inbox, and DLQs; select failure scenarios; reconcile uncertain payments; retry compensations. No direct state editing to Confirmed or Succeeded.

## Minimum model
Catalog:
- Tour(Id UUID, Name, Description, Active, Version).
- TourSession(Id UUID, TourId, StartsAtUtc, UnitAmountMinor long, Currency='MXN', Capacity, AvailableSeats, PriceVersion, Version).
- Quote(Id, unique RequestId, SessionId, Participants, UserId, UnitAmountMinor, TotalAmountMinor, Currency, ExpiresAtUtc).
- SeatHold(Id=HoldId, SagaId, SessionId, Participants, QuoteId, Status, ExpiresAtUtc, Version).
Reservations:
- Reservation(Id, UserId, SessionId, Participants, QuoteId, immutable agreed price, Status, CreatedAtUtc, Version).
- ReservationSaga(Id, ReservationId, State, HoldId, PaymentOperationId, RefundOperationId, DeadlineUtc, Version, FailureReason, required/completed compensations).
- IdempotencyRequest(UserId, OperationName, Key, RequestHash, ResourceId, ResponseStatus, ResponseBody).
- TourProjection(SessionId, Name, informational price, PriceVersion, UpdatedAtUtc).
Payments:
- PaymentOperation(Id, SagaId, ReservationId, AmountMinor, Currency, Status, ProviderReference, NextReconcileAtUtc, Version).
- RefundOperation(Id, PaymentOperationId, AmountMinor, Status, Version).
- FakeProviderOperation(unique OperationId, Kind, RequestHash, Status, reference).
Notifications:
- Notification(Id, UserId, ReservationId, SourceEventId, Kind, Subject, Body, Status, CreatedAtUtc, SentAtUtc).
Gateway identity: its own users, roles, and Identity tables, without joins to business-service tables.
Each consumer has an Inbox; each publisher has an Outbox. Reservations maintains Saga transition history.

## Required rules
R-01 Money: integer minor units (cents), MXN only, positive amounts, checked multiplication. Do not use double for money.
R-02 Participants must be between 1 and 6; the session must be in the future; no overselling. Capacity cannot be reduced below occupied or held seats.
R-03 A Quote lasts 5 minutes. HoldSeats must arrive before it expires. A valid quote preserves the accepted price even if the current price changes. Deactivating a tour prevents new holds.
R-04 A Hold lasts 10 minutes from creation. Confirmation requires Held status and must occur before expiration. Server UTC time is authoritative; clients do not determine expiration.
R-05 Confirmed requires a successful charge and confirmed seats. Unknown never means Declined.
R-06 A user may make multiple reservations for the same session. Retries of one intention use the same idempotency key.
R-07 User cancellation is allowed only from Confirmed and before StartsAtUtc. Cancelling a nonterminal request returns 409; do not build that additional workflow in the MVP. Repeating an already initiated cancellation returns the same process without another refund.
R-08 Cancellation is complete only after both the refund and seat release are confirmed. Show CancellationPending while either is outstanding; use ManualReview when automatic recovery cannot complete.
R-09 A terminal reservation failure without a charge ends in Failed after releasing any hold. A charge that cannot lead to confirmation ends in Failed after refund and release. Do not publish success prematurely.
R-10 No actual email is sent: the simulated sender records delivery. The UI labels payments and email as simulated.
R-11 Payments use a stable identifier per operation. A refund uses a different stable identifier linked to the original charge.
R-12 Persisted states survive process restarts and environment shutdown. Preserve queues and the database when pausing.

## MVP and essential topics
Implement success and persistence first, then failure invariants. Blazor must cover every human operation in the contract. The local projection is informational; it never authorizes price or capacity by itself. The lab's value is the complete workflow and recovery, not the number of projects.
