# LAB-005 execution and demo

Run from the repository root with PowerShell 7, SDK 10.0.401 and Docker Desktop (Linux engine):

```powershell
dotnet tool restore
./scripts/Start-Local.ps1
./scripts/Verify-Local.ps1
./scripts/Verify-Sagas.ps1
./scripts/Verify-Messaging.ps1
./scripts/Verify-Restart.ps1
```

Start-Local refreshes private runtime configuration, runs the separate privileged provisioner,
applies Catalog/Reservations/Payments LAB-005 migrations, and builds five images. No runtime host
migrates on startup. Existing rows, queues, keys and passwords are retained. The Saga migration
backfills payment-request/resolution flags using only Reservations' existing Outbox and state.
Verify-Sagas pauses all four consumers and all application-work processors, drives selected work
through unique real ElasticMQ evidence queues, and resumes hosted work in finally. Verification
retains evidence data. Never remove volumes or purge queues to obtain a clean run.

The secret-free shapes are in `config/private.example.json`. Reservations accepts SagaRecovery
AvailabilitySeconds=30, PaymentSeconds=120, ConfirmationSeconds=30, RecoverySeconds=120 by default.
Payments accepts PaymentWork.ReconcileIntervalsSeconds=[5,10,20,30,30], exactly five bounded values.
Private host/container JSON can override these settings; refreshing configuration regenerates it.
Compose's LAB_WORK_ENABLED switch also controls persisted Saga deadline discovery. Local generation
enables bounded lab controls only on Catalog, Payments and Notifications. Cloud remains disabled
until an explicit deployment task. TLS validation and schema/role isolation remain in force.

Open https://localhost:8443 and sign in using private seeded credentials. Payment, refund and
notification screens explicitly label fictional effects. Blazor calls authenticated Gateway APIs;
the browser receives neither provider credentials nor raw broker access.

## Cancellation

1. As Alice, reserve a future session and wait for Confirmed.
2. Open its detail and select Cancel with a full simulated refund.
3. Observe CancellationPending; inspect payment detail for the fixed refund ID and Pending/Refunded.
4. Wait for Cancelled and verify capacity returns once. Reload and repeat the HTTP intention with
   the same or a different key: it returns the existing process. Bob cannot read or cancel it.
5. Simulated notifications includes one ReservationCancelled notice.

## Charged confirmation failure

1. As Admin, open `/admin/simulations`, reset Payments to Success, and select Reject next valid
   confirmation under Catalog. This control is global to the lab: avoid concurrent demo reservations.
2. Create a reservation. Catalog rejects the actual ConfirmSeats after the real fictional charge.
3. Open `/admin/sagas`; observe refund/release requirements and completion independently.
4. Only after both complete does the reservation become Failed and create a failure notification.

## Transient refund failure

1. Select Payments mode RefundTransientFailure with one occurrence, then create a reservation.
2. After Confirmed, cancel it. The payment's persisted mode follows its refund.
3. Inspect Pending refund and attempts while the provider durably records exactly two transient
   failures; the third attempt succeeds with the same ID. No real refund is processed.
4. If compensation reaches ManualReview, use Admin Saga Retry missing compensation. Completed
   release/refund flags remain complete and IDs remain fixed.

## Uncertainty and late recovery

1. Select UnknownUntilAdminResolution with one occurrence and create a reservation.
2. Observe Unknown, then PaymentUncertain at the two-minute deadline, seat release, and ManualReview
   at the subsequent recovery deadline. Unknown never claims NotCharged.
3. On payment detail as Admin, select Resolve simulated provider as charged (or not charged).
   The control changes only the fictional provider and then requests actual reconciliation.
4. Charged resolution after abandonment triggers a refund and never confirms abandoned seats;
   NotCharged finishes Failed only after release. Consult the timeline for recovery decisions.

The deterministic late-held, hold-expiry, confirmation-timeout and post-Failed repair examples are
in `tests/Lab.Tests/SagaWorkflowTests.cs`. Run Verify-Sagas instead of waiting ten minutes or using
public crash hooks. Its post-Failed example deliberately seeds a local recovery-handler fixture;
it is not an end-to-end provider contradiction simulation. Restart verification stops/starts actual
Compose services during compensation after an independently committed provider refund.

Interactive click-through verification and simultaneous browser circuit isolation are separate
manual/LAB-007 checks. Passing handler/transport tests does not certify browser interactions or AWS.
