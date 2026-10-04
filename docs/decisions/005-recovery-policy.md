# LAB-005 recovery policy

Specifications 03/04 remain authoritative. LAB-004 independent review was PASS; its unfinished
refund execution is the declared LAB-005 boundary, not a prerequisite defect. No inventory,
money, security or cost rule has been replaced.

Payment reconciliation belongs to Payments. After the initial uncertain call, it performs at most
five status lookups with configured intervals, default 5/10/20/30/30 seconds. A Saga reconciliation
command can make eligible work due but cannot reset this counter or initiate another charge.
An authorized, audited human reconciliation starts a fresh bounded lookup window. The Saga
waits two minutes for payment (never beyond hold validity), then releases seats and enters
PaymentUncertain; a further persisted recovery deadline moves unresolved work to ManualReview.
ManualReview asserts uncertainty, never absence of a charge. Entering review preserves the original failure reason; history records the automatic-recovery deadline or refund-review reason. This also keeps an old compatible decline recognizable after deadline escalation. If workers were paused, it is honest
to expose ManualReview even before all provider lookups have run: pending payment work persists.

Refunds retry at most five attempts per automatic window. Each attempt queries status before
calling the fictional provider with the original ID. Timeout leaves work Pending; exhaustion
publishes RefundNeedsReview once per authorized retry window; each newer exhausted window has a distinct review message, while refund completion remains one business result. Admin retry sends a new command message with the same operation
IDs; it resumes only incomplete flags. RefundPayment carries optional RetryVersion (default zero for the original contract); Payments persists LastRetryVersion. A newer Saga Admin retry may reopen an exhausted window, while different MessageIds or stale retry versions cannot reset its budget. This metadata never changes the provider money fingerprint. A previous Refunded/Released response may be sent again
with its original MessageId, allowing repair without repeating an inventory or money effect.
This is duplicate delivery, not exactly-once transport.

Saga row locks serialize consumers, deadlines and HTTP actions across replicas. EF Version
concurrency tokens remain an additional write check. Inbox, state, flags, history and Outbox
commit together. Deadline workers discover rows with SKIP LOCKED; no process timer owns a Saga.
Completion requires release plus any required refund and a definitive payment outcome if payment
was requested. Terminal notice keys prevent a repaired Failed Saga from publishing another notice.

The cancellation request key is scoped by actor and operation. Its original response is 202
CancellationPending, including retries after completion; follow Location for the current state.
Different keys against the same reservation join the fixed cancellation process. Once initiated,
departure does not prevent its completion or replay. Before initiation, departure and non-Confirmed
states return 409. Non-owners receive 404; authorized Admin may cancel before departure.

Catalog retains its existing Hold → Session lock order, expiration, conditional inventory updates,
and release tombstones. RejectNextConfirmation rejects an actual valid confirmation request;
the fault occurrence and rejection intent commit together. No success event is fabricated by UI.

Full diagnostics, DLQ inspection/replay UI, other service pause/timeout modes and telemetry expansion
remain LAB-006. This task does not silently consume prior DLQ messages or purge queues.



