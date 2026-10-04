# LAB-003 Independent Review Report

**Date**: October 3, 2026  
**Subject**: TourMicroservicesLab — LAB-003 Durable Reservations, Outbox/Inbox & SQS Messaging  
**Target Solution**: `TourMicroservicesLab.slnx` (.NET 10.0.401, PostgreSQL 17.6, ElasticMQ 1.6.15)  

---

## 1. Verdict

### **PASS**

Codex's implementation of **LAB-003** fulfills all functional, transactional, architectural, and reliability requirements specified for the Reservations service, transactional Outbox/Inbox patterns, and local SQS transport.

All automated test suites executed against real PostgreSQL 17.6 and ElasticMQ 1.6.15 passed with zero failures:
- **Standard Local Suite (`scripts/Verify-Local.ps1`)**: **45 passed, 0 failed, 9 skipped** (the 8 messaging tests and 1 restart test are dedicated to their own scripts).
- **Controlled Messaging Suite (`scripts/Verify-Messaging.ps1`)**: **8 passed, 0 failed, 0 skipped** (real PostgreSQL rollback, crash windows before/after send, lease contention, commit-before-ack redelivery, poison DLQ redrive, and 503 dependency isolation).
- **Cold Docker Restart Suite (`scripts/Verify-Restart.ps1`)**: **1 passed, 0 failed, 0 skipped** (complete MD5 snapshots of all 5 Catalog tables and all 7 Reservations tables, Identity users, active session cookies, and broker evidence message survive Docker Compose stop/start).

Operator manual browser verification of live hold workflow, idempotency key safeguards, 404 ownership isolation, and final-seat competition (Alice AwaitingPayment vs. Bob Failed InsufficientSeats) was confirmed by the human operator.

The solution is ready for **LAB-004**.

---

## 2. Acceptance Matrix

| Requirement / Specification | Status | Evidence / Verification Notes |
|---|---|---|
| **AC-03: HTTP Idempotency & Creation Atomicity**<br>Concurrent creation requests with the same key produce one reservation; replays return the recorded 202/Location; different payload under the same key returns 409 `IdempotencyConflict`. | **PASS** | `ReservationHttpTests.ConcurrentHttpCreationOwnershipReplayAndLiveHoldWorkflowStopAtAwaitingPayment`: two concurrent POSTs result in one idempotency row and matching 202 responses. Handled via `ReservationCreator.CreateAsync` with unique key rollback handling. |
| **Catalog Quoting & 503 Isolation**<br>Quotes use dedicated RS256 Reservations token; HTTP call executes outside DB transaction; Catalog downtime returns 503 without creating reservations, sagas, or payments. | **PASS** | `MessagingTests.RealCatalogFailureReturns503AndCreatesNoDurableReservationWork`: stops Catalog container; POST returns 503; verified 0 idempotency rows, 0 reservations, and 0 sagas created. Stale accepted price returns 409 (`StaleAcceptedPriceReturnsConflictWithoutReservationWork`). |
| **AC-04: Multi-User Competition for Final Seat**<br>Two users competing for the final seat via HTTP and SQS results in one `AwaitingPayment` reservation and one `Failed` reservation; available seats reaches 0 without overselling; rejected saga emits no payment intent. | **PASS** | `ReservationHttpTests.TwoUsersCompeteThroughHttpAndSqsForTheFinalSeat`: concurrent requests processed through real SQS and Catalog/Reservations hosted workers. |
| **AC-07: Crash After Commit / Pending Outbox Recovery**<br>Pending Outbox rows commit with the business entity; fresh workers claim the durable intent and dispatch it over SQS. | **PASS** | `MessagingTests.RecoveryAfterReservationCommitDispatchesDurableIntentAndLeavesPaymentCommandQueued`: commits reservation rows with workers paused; fresh dispatcher/consumers discover persisted intent and complete hold workflow to `AwaitingPayment`. |
| **AC-08: Send-Before-Mark Recovery & Deduplication**<br>Crash after SQS `SendMessage` but before marking Outbox published allows lease expiration, recovery by replacement worker, and duplicate redelivery without double seat deduction. | **PASS** | `MessagingTests.SendBeforeMarkCrashRecoversExpiredLeaseAndDuplicatesDoNotDeductTwice`: simulated crash after send; recovered lease resends identical `MessageId` and `DeliveryId`; Catalog Inbox recognizes duplicate and does not decrement seats twice. |
| **AC-09: Consumer-Commit-Before-ACK Redelivery**<br>Crash after database commit but before SQS `DeleteMessage` redelivers message; consumer recognizes duplicate via Inbox and idempotently ACKs without repeating business transitions. | **PASS** | `MessagingTests.ConsumerCommitBeforeAckCrashRedeliversWithoutRepeatingInventoryOrSagaTransition`: simulated crash before delete; SQS redelivers; Inbox duplicate check ensures transition history and payment intent are not duplicated. |
| **Local Atomicity: Inbox + Business Effects + Outbox**<br>Consumer failure during local business effects rolls back Inbox record, state changes, and outgoing Outbox intents together in a single local transaction. | **PASS** | `MessagingTests.FailedEffectsRollBackInboxInventoryAndOutboxTogether`: simulated crash during effect rolls back Inbox, Hold row, and outgoing intent together. Handled by `InboxTransaction.ProcessAsync`. |
| **Leased Outbox Claiming & Competing Publishers**<br>Outbox claiming uses `SKIP LOCKED` with bounded leases; multiple publishers claim distinct deliveries; expired lease owner cannot mark another worker's delivery published. | **PASS** | `MessagingTests.CompetingPublishersClaimOneLeaseAndExpiredOwnerCannotMarkAnotherLease`: two concurrent claims select one row; expired owner cannot mark published; replacement owner successfully sends and marks published. |
| **AC-23 (Partial): Poison Message & DLQ Boundary**<br>Malformed or unsupported messages are not silently acknowledged; broker redrive moves message to DLQ after 5 receives. | **PASS** | `MessagingTests.UnsupportedPoisonMessageIsNotAckedAndBrokerMovesItToDlqAfterFiveReceives`: unsupported message throws `PoisonMessageException`, visibility reset to 0, received 5 times, broker moves to DLQ. |
| **AC-18 (Partial): Projection Intent Ordering**<br>Existing `TourSessionChanged` intents update informational `TourProjections`; older versions cannot overwrite newer versions. | **PASS** | `MessagingTests.CatalogProjectionIntentDispatchesAndOlderVersionCannotOverwriteLatest`: dispatches version 1, 3, then 2; projection retains version 3. |
| **AC-20 (Partial): Ownership & Authorization**<br>Users can only view their own reservations and timelines; unauthorized access returns 404; Admin can list, filter, and inspect any user's reservations. | **PASS** | Verified in `ConcurrentHttpCreationOwnershipReplayAndLiveHoldWorkflowStopAtAwaitingPayment`: Bob receives 404 for Alice's reservation detail/timeline; Admin receives 200. |
| **AC-28 (Partial): Blazor Reservations UI & Polling**<br>Submission freezes intention and key during retries; deliberate new intention generates fresh key; active detail polls every 3s and joins polling on disposal; pending payment explicitly labeled. | **PASS** | Verified in `ReservationSubmission.razor`, `Reservations.razor`, and `ReservationDetail.razor`. Client uses per-circuit identity and fresh per-request bearer headers without retaining DbContext. |
| **Restart Durability**<br>All database tables in Catalog and Reservations, Identity users, active session cookies, and broker evidence messages survive cold Docker Compose stop/start. | **PASS** | `DatabaseQueueMessageAndLoginCookieSurviveLocalStopStart` in `PersistenceTests.cs` executed via `scripts/Verify-Restart.ps1`. MD5 hashes of all 12 business tables match before and after restart. |

---

## 3. Findings and Observations

### Defect Findings: None (0 Critical, 0 High, 0 Medium, 0 Low)
No functional, security, or architectural defects were identified in the LAB-003 implementation.

### Architectural & Verification Observations (Informational / Deferred)

1. **Payments and Notifications Workers Staged for LAB-004 (Expected Boundary)**
   - `ProcessPayment` and `ReservationFailed` commands are durably created and published to SQS queues `tourlab-payments` and `tourlab-notifications`.
   - As required by the LAB-003 specification boundary, Payments and Notifications transport workers are not registered, ensuring pending messages remain safely queued in SQS without premature acknowledgement or DLQ movement.
   - *Disposition*: Ready for LAB-004 consumer implementation.

2. **Catalog Expiration vs. Reservations Compensation (Scheduled for LAB-005)**
   - Catalog runs `HoldExpirationWorker` to expire held seats and emit `SeatsHoldExpired`.
   - In Reservations, handling of `SeatsHoldExpired`, reservation deadlines, and compensation workflows is scheduled for LAB-005. If such an event arrives in Reservations during LAB-003, it throws `PoisonMessageException("ReservationsHandlerNotImplemented")` and moves to DLQ.
   - *Disposition*: Preserves defensive bounds; aligns with task plan.

3. **Automated Headless Browser UI Testing Limitation (Carried Over from LAB-001/002)**
   - Playwright browser execution remains blocked due to the Windows sandbox ACL initialization limitation (`windows sandbox failed: helper_unknown_error: apply deny-read ACLs`).
   - *Disposition*: Verification is covered via comprehensive HTTP integration tests and manual operator instructions in `docs/reservations-execution.md`. Automated browser testing and multi-user circuit isolation (AC-26) remain scheduled for LAB-007.

4. **Gateway Upstream Ingress Rate Limiter (Carried Over from LAB-001/002)**
   - Rate limiting continues to partition by direct connection IP.
   - *Disposition*: Non-blocking for direct-access local/AWS topology; deferred to LAB-007/008.

---

## 4. Executed Checks Summary

All verification checks were run against the live local Docker environment:

| Command | Scope | Outcome |
|---|---|---|
| `powershell -File ./scripts/Verify-Local.ps1` | Full local test suite | **45 passed, 0 failed, 9 skipped** (59s runtime). |
| `powershell -File ./scripts/Verify-Messaging.ps1` | Controlled messaging & crash windows | **8 passed, 0 failed, 0 skipped** (42s runtime). |
| `powershell -File ./scripts/Verify-Restart.ps1` | Cold Docker Compose stop/start | **1 passed, 0 failed, 0 skipped** (30s runtime; zero data loss). |
| `dotnet ef migrations has-pending-model-changes` | Catalog, Reservations & Identity models | **No model changes pending** across all 3 DbContexts. |
| `docker compose ps` | Container health checks | All 7 containers healthy and running. |

---

## 5. Unverified Items & Boundaries (Scheduled for Later Labs)

The following areas are explicitly outside LAB-003 scope:
- **Payment Provider Fictional Operations & Confirmation (AC-02, AC-05, AC-10, AC-24)**: Scheduled for LAB-004.
- **Saga Compensations, Expiration Timers, and Cancellations (AC-06, AC-11, AC-12, AC-13, AC-14, AC-15)**: Scheduled for LAB-005.
- **DLQ Inspection & Replay Tools (AC-23 complete)**: Scheduled for LAB-006.
- **Automated Browser E2E & Multi-User Circuit Isolation (AC-26)**: Scheduled for LAB-007.
- **AWS Infrastructure & Cloud Deployment (AC-30)**: Scheduled for LAB-008 and LAB-009.

---

## 6. Handoff to Codex (Prioritized Next Steps for LAB-004)

Codex may proceed to **LAB-004**. The prioritized roadmap for LAB-004 is:

1. **Durable Fake Payments Service (`Payments.Api`)**:
   - Create `payments` database schema, migrations, and tables (`PaymentsDb`, `PaymentOperations`, `ProviderEffects`, `Outbox`, `Inbox`).
   - Implement `IPaymentProvider` with simulated modes: `Success`, `Decline`, `TimeoutAfterCharge`, `UnknownUntilAdminResolution`, `RefundTransientFailure`.
   - Register Payments SQS consumer worker for `ProcessPayment`: execute provider call outside DB transaction, persist result idempotently, and emit `PaymentSucceeded` or `PaymentDeclined` to Outbox.
2. **Reservations Payment Consumer**:
   - In `Reservations.Api`, consume `PaymentSucceeded` -> transition saga to `AwaitingConfirmation` and emit `ConfirmSeats` command to `tourlab-catalog`.
   - Consume `PaymentDeclined` -> transition saga to `Compensating` and emit `ReleaseSeats` command to `tourlab-catalog`.
3. **Catalog Confirmation Consumer**:
   - In `Catalog.Api`, consume `ConfirmSeats` -> confirm hold and emit `SeatsConfirmed`.
4. **Reservations Confirmation Consumer & Notifications**:
   - In `Reservations.Api`, consume `SeatsConfirmed` -> transition saga to `Confirmed`, reservation status to `Confirmed`, and emit `ReservationConfirmed` to `tourlab-notifications`.
   - In `Notifications.Api`, implement `NotificationsDb`, durable receipt tracking by `SourceEventId + Kind`, and fake sender worker.
5. **Blazor UI Extensions**:
   - Add Payments and Notifications screens in `Gateway.Web` to view simulated charges and notification receipts.
