# LAB-004: independent simulated effects and safe staged compensation

LAB-003 independent review is PASS. Existing transport and Catalog inventory handlers are reused.
Payment consumption commits accepted Pending work and Inbox before acknowledgement. A separately
claimed worker queries/calls the fictional provider with the original operation ID outside the
application transaction. The provider uses its own factory-created context/transaction and unique
operation identity. A crash after its durable effect is therefore visible and recoverable by lookup.
Notification consumption and its independent fictional sender follow the same separation.

Success requires actual Catalog confirmation. Decline enters Compensating, requests release and
finishes Failed only after SeatsReleased. Unknown remains unresolved and reconciliation uses lookup,
never a new charge. Provider modes are snapshotted per accepted operation; fault configuration has
bounded occurrences and audited Admin changes. No real money/email integration is used.

Specification W-04 requires refund and release intents after a charged confirmation rejection.
This is a minimum safety prerequisite for LAB-004: persist Compensating, required/completed flags,
reason, original RefundOperationId and both intents. Refund execution and full compensation are
LAB-005; unsupported RefundPayment remains unacknowledged and may enter DLQ. Never finish Failed
or publish failure while a required refund remains unresolved. Late success/expired holds are
quarantined for LAB-005 repair; they never become Confirmed without valid seats. Deadline-driven
abandonment, refunds, cancellation and complete AC-06 are explicitly unfinished.
