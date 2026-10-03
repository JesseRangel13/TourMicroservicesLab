# 04 — Workflows, Transactions, and Recovery

## W-01 Creation and success
1. Authenticate/authorize and validate the request. Compute a canonical content hash. Look up idempotency results by user + operation + key.
2. Request an idempotent quote from Catalog; verify the accepted price and validity. Do not hold a database transaction open during HTTP.
3. Reservations transaction: insert the unique key, reservation, AwaitingAvailability Saga, and HoldSeats intent into Outbox. Resolve concurrent requests through the unique constraint and read the winning result after rollback.
4. Catalog consumes HoldSeats: record Inbox and validate the quote; decrement AvailableSeats through a conditional update requiring AvailableSeats>=Participants, insert the Hold, and add SeatsHeld to Outbox within the same transaction. An inventory conflict produces SeatsHoldRejected. Never use an unprotected SELECT followed by UPDATE.
5. Reservations consumes SeatsHeld: transition to AwaitingPayment and add ProcessPayment to Outbox; use the already persisted ID. The payment-resolution deadline is 2 minutes from the request, without exceeding the hold's validity.
6. Payments persists a Pending request and ACKs after commit. A separate worker queries/calls the fake provider outside any long database transaction. The provider persists its effect with a unique key in its own transaction; then Payments saves the result and Outbox. If the process crashes between those steps, reconcile by operation ID.
7. PaymentSucceeded → AwaitingConfirmation + ConfirmSeats.
8. Catalog atomically confirms an unexpired Held record and adds SeatsConfirmed to Outbox. Do not change AvailableSeats on confirmation: holding already deducted them.
9. SeatsConfirmed → Confirmed + ReservationConfirmed for Notifications. Inbox + state + history + Outbox always belong to each consumer's local transaction.
10. Notifications creates a Pending record and ACKs. The fake sender worker durably records delivery idempotently by business event and marks Sent. Its UI shows Simulated and never claims actual SMTP delivery.

## W-02 Capacity, quote, or payment failure
Invalid/expired quote: 409 before creating the Saga. Catalog unavailable: 503 before the Saga.
SeatsHoldRejected: Failed without payment. PaymentDeclined: initiate ReleaseSeats and remain Compensating; after SeatsReleased, transition to Failed. Preserve the reason.
Catalog expires Held records through a worker, with an atomic transition and exactly one capacity restoration; publish SeatsHoldExpired. A later release recognizes that release already occurred and emits the required confirmation without restoring capacity again.

## W-03 Timeout after a charge
Fake mode TimeoutAfterCharge: the provider durably records Succeeded and then returns/throws a timeout once. PaymentOperation becomes Unknown, not Declined. The worker queries the provider using the same ID; finding Succeeded publishes PaymentSucceeded once per business transition.
If there is no verifiable result by the deadline, mark the Saga PaymentUncertain and request ReleaseSeats; prevent late confirmation. Reconcile with at most 5 queries and configurable intervals (for example 5, 10, 20, 30, 30 seconds), then ManualReview. The hold has an independent expiration. An unresolved Unknown never becomes Failed while asserting that no charge occurred.
If success arrives after the decision to release/expire the hold, request a refund using the predefined RefundOperationId. Do not confirm an abandoned reservation. If the definitive result is Declined/NotCharged, finish Failed after releasing capacity. Do not resolve ambiguity with a new ProcessPayment operation.

## W-04 Compensation after confirmation failure
SeatsConfirmationRejected after a charge: enter Compensating and persist RefundPayment and ReleaseSeats in Outbox. Maintain two durable flags: RefundCompleted and SeatsReleased. Response order does not matter. Finish Failed and notify only after both flags are true. Payments and Catalog do not share a transaction.
Compensation may fail. Expose ManualReview, the reason, and pending operations. Admin retry reuses identifiers and continues from the flags; it never repeats the charge or creates another full refund.

## W-05 Cancellation
From Confirmed and before the session starts: a transaction fixes one RefundOperationId, sets CancellationPending, and adds RefundPayment and ReleaseSeats. Set Cancelled only after both results. Endpoint and workflow idempotency protect double clicks and different cancellation keys for the same resource. The MVP refunds the full amount without fees. Release may release a Confirmed hold in this workflow; restore capacity once. After departure time, return 409.

## W-06 Waiting for availability and late results
AwaitingAvailability has a configurable 30-second deadline. If no result arrives, start abandonment using ReleaseSeats with the original HoldId; finish Failed after release confirmation. A late SeatsHeld during abandonment does not start payment: keep the release in progress. Catalog handles ReleaseSeats before HoldSeats through a persistent HoldId tombstone, so a late hold command cannot recreate the hold. This also protects against out-of-order replay. Retain tombstones for the same deduplication period as Inbox. Do not finish on timeout while allowing a future hold to remain uncleared.
AwaitingConfirmation has a 30-second deadline. When it expires, initiate charge compensation and seat release; a late confirmation does not return the Saga to Confirmed. If SeatsConfirmed arrived first and won the transition, the timer reloads state and does not compensate. Use Version and transitions, not cross-replica in-memory locks.

## State and out-of-order messages
Minimum states: AwaitingAvailability, AwaitingPayment, PaymentUncertain, AwaitingConfirmation, Confirmed, Compensating, CancellationPending, Cancelled, Failed, ManualReview.
Every transition checks expected state, related identifiers, and Version. History persists from/to, reason, timestamp, and messageId. Do not advance based only on an event name.
An old, compatible event whose effect is already satisfied becomes an audited no-op + ACK. Impossible events or contradictory data generate an alert/DLQ entry rather than overwriting terminal state. A late payment success after abandonment requires compensation, even if the process already ended Failed with no known charge. It may reopen repair as Compensating; do not change it to Confirmed. Preserve history and do not publish another duplicate ReservationFailed at repair completion.
Expiration/confirmation race: Catalog serializes changes on Hold + Session. Confirmation requires Held and expiresAt>now. Only one transition wins. Duplicate expiration/release/confirmation must not alter inventory twice. Use a consistent locking order to avoid deadlocks.

## Outbox and Inbox
Outbox: claim batches with a lease/SQL SKIP LOCKED or a tested equivalent. Lease expiration enables recovery. Send outside the transaction and record publication with an ownership check. Two destinations mean two deliveries, not two business events.
Inbox: unique(ConsumerName, MessageId); insert + effects + Outbox in one transaction. Detect PostgreSQL duplicate conflicts after rollback; do not keep using an aborted transaction. ACK only after commit or a safe no-op.
Business idempotency: unique PaymentOperationId/RefundOperationId/HoldId, plus state checks. New messages concerning the same operation do not create another effect.
Retain deduplication data at least as long as queue + DLQ retention; the lab retains it for 14 days. Do not purge before 14 days or during normal pause. Replaying older messages requires reviewing the retention boundary; do not promise eternal protection.

## Per-service lab failure modes
Catalog: None, QuoteTimeout (bounded delay), RejectNextConfirmation, PauseConsumption. Reservations: None, PauseOutbox. Payments: the provider modes below and PauseConsumption. Notifications: None, FailureThenSuccess. Persist configuration per service, with limited occurrences and auditing. Pausing never purges queues or removes Inbox. Admin may reset modes. Crash points are test barriers/scripts, not UI-accessible crash endpoints.

## Fake provider and sender
IPaymentProvider: ChargeAsync, GetChargeStatusAsync, RefundAsync, GetRefundStatusAsync. All accept CancellationToken and a stable ID. Modes: Success, Decline, TimeoutAfterCharge, UnknownUntilAdminResolution, RefundTransientFailure (2 failures, then success). Persist the mode per operation when creating it; changing configuration does not magically alter an existing charge.
Admin resolution of UnknownUntilAdminResolution modifies only the authorized fictional provider result, with auditing and a clearly simulated UI. Then perform actual workflow reconciliation rather than editing Saga state. Add POST /v1/admin/fake-provider/{operationId}/resolve with Succeeded or NotCharged and the corresponding UI control.
INotificationSender fake stores a durable unique receipt by SourceEventId + Kind. The notification operation also checks ReservationId + Kind to avoid creating the same terminal notice again through a different event. FailureThenSuccess tests 2 transient failures. Inbox must not suppress Pending work that failed after message consumption.
