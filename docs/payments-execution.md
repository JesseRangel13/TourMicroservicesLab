# LAB-004 execution and manual demo

Run from the repository root in PowerShell with the existing private `.local` files and trusted lab CA.
No AWS credentials, SMTP or payment account are needed. Preserve all volumes and queue contents.

```powershell
dotnet restore --locked-mode
./scripts/Start-Local.ps1
./scripts/Verify-Local.ps1
./scripts/Verify-Payments.ps1
./scripts/Verify-Messaging.ps1
./scripts/Verify-Restart.ps1
docker compose ps
```

Startup refreshes existing private JSON, uses isolated migrator roles for all five schema migrations,
initializes/reuses the four queues/DLQs, and builds/starts five images. The additive Reservations migration
adds charged-confirmation compensation requirements and completion flags. Payments owns Operations,
ProviderEffects, Inbox, Outbox, Faults and Audit; Notifications owns Notifications, DeliveryReceipts,
Inbox, Outbox, Faults and Audit. No service reads another service's tables. Runtime pools remain five.
Provider/sender commits use separate factory-created contexts, so their durable effects survive a
worker crash before result recording. All receipts/references are explicitly fictional.

All four SQS consumers and Outbox dispatchers are now enabled. Payments accepts ProcessPayment,
commits Pending work before ACK and processes it through recoverable SQL leases. Its provider mode
is snapshotted at acceptance. Lookup precedes charge on Pending recovery; Unknown recovery uses
lookup only. Unknown auto-reconciliation makes bounded attempts (five counter increments, five-second
intervals), retaining Unknown when unresolved. Admin reconciliation schedules another audited lookup
with the original operation ID. It does not create a new charge or edit a Saga outcome.
Notifications accepts ReservationConfirmed/ReservationFailed, commits Pending before ACK and runs
an independently durable sender. FailureThenSuccess fails twice, then records one fictional receipt.
Its five-second retry delay and SQL leases allow restart/multiple-worker recovery.

For controlled tests, Compose honors `LAB_MESSAGING_ENABLED=false` for all consumers/Outbox workers
and `LAB_WORK_ENABLED=false` for payment/sender processors. Scripts restore them in finally and do
not purge queues. Run the scripts sequentially, outside a demo. Controlled tests use uniquely named
retained evidence queues and fresh worker instances with crash callbacks; there are no public crash
endpoints. Restart verification stops/starts the actual lab and preserves Pending work/effects/data.

## APIs and lab controls

Use existing validated user JWTs through Gateway `/api/payments` and `/api/notifications` routes.
GET `/v1/payments?reservationId=&page=&pageSize=` returns own rows (Admin may read all); GET
`/v1/payments/{id}` returns simulated outcome, mode, reference and reconciliation state.
POST `/v1/admin/payments/{id}/reconcile` is Admin-only, accepts Unknown operations durably with 202,
and returns 409 for other states. No refund is reported as executed.
GET `/v1/notifications?reservationId=&page=&pageSize=` and `/v1/notifications/{id}` expose own simulated
content/status/receipt time; Admin access is explicit. Another tourist's detail is 404, list is filtered.

Both services provide Admin GET/PUT `/v1/admin/faults` and POST `/v1/admin/faults/reset`, guarded by
LabFeaturesEnabled and a per-validated-user limit of 20 requests/minute. PUT body is mode/occurrences
(0–100). Payments supports Success, Decline, TimeoutAfterCharge; Notifications supports None and
FailureThenSuccess. Configuration, occurrence consumption and audit rows are durable. Reset does
not alter accepted operations. Local config generation enables these two simulated controls; external
config should leave LabFeaturesEnabled false. The secret-free example retains that safe default.

Unsupported RefundPayment/ReconcilePayment queue commands remain unacknowledged for staged
LAB-005/LAB-006 handling. Admin reconciliation currently schedules the local durable operation;
there is no need for the future orchestrator ReconcilePayment command yet. ReservationCancelled,
refund result and hold-expiration workflows are unfinished. Unsupported types are never silently ACKed.

## Manual demo — new LAB-004 interactive browser steps are NOT VERIFIED

Open https://localhost:8443 using trusted TLS. Sign in with the private seeded accounts.

1. Success: Admin opens Administration → bounded simulated controls, chooses Payments/Success/1
   and applies. Alice reserves a future session, selects participants and accepts the price. Open its
   detail: AwaitingPayment → AwaitingConfirmation → Confirmed. Follow the payment and notification
   links: one simulated Succeeded operation/reference and one simulated Sent notice. Fast transitions
   may only be visible in the durable timeline. Catalog capacity is deducted by hold, not again by confirm.
2. Decline: Admin selects Payments/Decline/1. Submit a deliberate new reservation. Payment becomes
   Declined; reservation becomes Compensating then Failed only after SeatsReleased. Inspect timeline,
   failure reason, restored seats and the simulated failure notification. No successful charge exists.
3. Timeout after charge: Admin selects Payments/TimeoutAfterCharge/1, then creates a new reservation.
   Payment temporarily shows Unknown while its provider has a durable simulated charge. Read-only
   detail polls every three seconds; automatic lookup usually resolves after five seconds. Admin may
   press Reconcile original provider operation while Unknown; a raced 409 means it already resolved.
   It becomes Succeeded and the reservation Confirmed without a second charge. No full AC-06 claim is
   made: never-resolving outcomes, deadline/late-success repair and refund workflows remain LAB-005.
4. Sender failure: Admin selects Notifications/FailureThenSuccess/1 before a new terminal reservation
   event. Its notification remains Pending through two simulated failures, then Sent with one receipt.
5. Bob cannot list/read Alice's payment or notice; entering another owner's detail URL is 404. Admin
   can inspect them. Filters/pagination, session expiry, visible API errors and navigating away from
   active polling screens should be checked. No UI can publish PaymentSucceeded or force Confirmed.

Each selection affects the next bounded number of newly accepted operations, including any existing
queue backlog. Use a quiet lab for a demonstration; changing the control never changes an already
accepted operation's mode. Reservation submission retains its original intention on uncertain retry;
the existing circuit-state/full-refresh limitation remains. No new browser/operator confirmation is
claimed and the known sandbox browser failure was not retried.

## LAB-005 safety boundary

Charged SeatsConfirmationRejected persists Compensating, its reason, original RefundOperationId,
RefundPayment and ReleaseSeats intents. SeatsReleased can complete while refund remains required;
the reservation stays Compensating and does not publish terminal failure. Refund commands may enter
DLQ because their handler is unfinished; preserve them for controlled replay after LAB-005.
Expired holds/late payment success are quarantined rather than confirmed. Old pending payment queue
contents are preserved and processed by stable IDs; outcomes tied to expired/abandoned holds need
LAB-005 repair. A payment success alone is never a confirmed reservation. Cancellation, complete
refund operations, deadline-driven abandonment, late-result compensation, unknown-provider resolution
and full Saga recovery are not implemented. No data or deduplication records are automatically deleted.
