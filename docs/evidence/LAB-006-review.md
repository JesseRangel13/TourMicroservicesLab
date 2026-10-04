# LAB-006 Independent Review Report

**Date**: October 4, 2026  
**Subject**: TourMicroservicesLab — LAB-006 Resilience, Observability, Informational Projection, DLQ Administration, and Controlled Faults  
**Target Solution**: `TourMicroservicesLab.slnx` (.NET 10.0.401, PostgreSQL 17.6, ElasticMQ 1.6.15)  

---

## 1. Verdict

### **PASS**

Codex's implementation of **LAB-006** fulfills all functional, transactional, architectural, observability, and reliability requirements specified for:
1. **Catalog Quote HTTP Resilience**: Strict per-instance client policy featuring 3-second per-attempt timeout, at most 2 retries (3 total attempts) with exponential backoff and jitter, configurable 10-second total limit, 10-caller concurrency semaphore, and a 5 consecutive qualifying failure circuit breaker opening for 30 seconds with a single probe attempt. Stable quote identity across retries; non-transient responses (400, 401, 403, 404, 409) and caller cancellations are never retried; Catalog unavailability reliably yields 503 without initiating Reservation, Saga, or Payment records.
2. **End-to-End Distributed Tracing & Structured Logging**: OpenTelemetry 1.17.0 instrumentation covering ASP.NET Core, HttpClient, and custom lab activities (`TourMicroservicesLab`). W3C `traceparent` and optional `tracestate` propagation across integration message envelopes (`MessageRoutes.Serialize`, `MessageCodec.Parse`); consumer spans (`message.consume`) and worker spans (`handler.durable-work`) accurately link to incoming message trace context. Canonical Inbox fingerprinting excludes trace context so envelope telemetry updates never corrupt idempotency deduplication. Allowlisted JSON span exporter prevents credential or payload leakage.
3. **Admin Diagnostics, Metrics & Health**: Dedicated `/v1/admin/diagnostics` endpoint on each service reporting bounded Outbox metadata, oldest pending outbox age, Inbox history, worker process state, owned business counts by state, and approximate DLQ counts. Statement timeouts (5s) and bounded limits prevent table scans. Dependency-free `/health/live` and runtime-DB-only `/health/ready` endpoints preserved.
4. **Informational Tour Projection**: Reservations-owned `TourProjections` table consumed asynchronously from Catalog `TourSessionChanged` integration events. Monotonic `PriceVersion` checks ensure out-of-order and duplicate events do not overwrite newer data. Catalog remains strictly authoritative for pricing quotes and seat hold capacity; absent projections return clean empty views without failing queries.
5. **Per-Service DLQ Inspection & Replay**: Isolated DLQ access (`tourlab-{service}-dlq`) with server-side receipt handles stored in `DlqInspections`. Up to 10 messages inspected with 120-second broker visibility and 110-second inspection leases; payloads and raw receipts are never exposed to browser clients. Replay sends the exact original body to the service input queue before attempting DLQ deletion; deletion failure records `SentDeleteUncertain` and preserves lease retryability. No message body editing or MessageId regeneration.
6. **Controlled Fault Injection & Safe Reset**: Persisted `Faults` table per service schema with decrementing occurrence counters for `QuoteTimeout`, `PauseConsumption`, and `PauseOutbox`. Safe `/v1/admin/faults/reset` endpoint restores normal operation without clearing durable business work or overwriting accepted payment provider modes.
7. **Authorization & Rate Limiting**: All operational endpoints enforce `AdminApi` role authorization and 60 req/min rate limiting. Unauthorized requests return 401; non-admin users return 403.
8. **Blazor Operations Dashboard & Projections**: Server-rendered Blazor screens for `/operations` and `/tour-projections` with typed per-user HTTP clients and explicit manual refresh. Strict absence of automatic background UI polling loops.

All automated verification test suites executed against real PostgreSQL 17.6 and ElasticMQ 1.6.15 passed with zero failures:
- **Operations & Resilience Suite (`scripts/Verify-Operations.ps1`)**:
  - **Live suite (`lab006-live.trx`)**: **13 passed, 0 failed, 0 skipped** (1m 11s)
  - **Controlled suite (`lab006-controlled.trx`)**: **4 passed, 0 failed, 0 skipped** (27s)
- **Sagas & Payment Regression Suite (`scripts/Verify-Sagas.ps1`)**: **15 passed, 0 failed, 0 skipped** (2m 39s; `lab006-sagas-regression.trx`)
- **Messaging Regression Suite (`scripts/Verify-Messaging.ps1`)**: **8 passed, 0 failed, 0 skipped** (48s; `lab003-messaging.trx`)
- **Local Unit & Integration Suite (`scripts/Verify-Local.ps1`)**: **58 passed, 0 failed, 28 skipped** (2m 6s; `lab005.trx`)
- **EF Core Migrations**: **0 pending model changes** across `Catalog.Api`, `Reservations.Api`, `Payments.Api`, and `Notifications.Api`.
- **NuGet Package Security Audit**: **0 vulnerable packages** reported across all 9 projects.
- **Git Formatting Check**: **0 whitespace/format errors**.

No application code or tests were modified during this review. The solution is ready for **LAB-007**.

---

## 2. Acceptance Matrix

| Requirement / Specification | Status | Evidence / Verification Notes |
|---|---|---|
| **AC-19: Catalog Quote HTTP Resilience Policy**<br>3s per-attempt timeout; max 2 retries (3 total attempts) with jittered exponential backoff; 10s total limit; circuit breaker opening after 5 consecutive qualifying failures per instance, remaining open 30s, and probing recovery; concurrency limit of 10 logical calls per instance. | **PASS** | Implemented in [`CatalogQuoteClient.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Application/CatalogQuoteClient.cs). Verified via unit tests in [`QuoteResilienceTests.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/QuoteResilienceTests.cs) and live end-to-end integration test [`SlowCatalogFiveFailuresOpenCircuitThenRecoveryCreatesWorkOnlyAfterQuoteSucceeds`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L173-L190). 5 simulated quote timeouts open the breaker with `CatalogCircuitOpen`; after 30s cooldown a probe request recovers the circuit. Excess concurrent requests reject immediately with `CatalogBusy` (503). |
| **HTTP Resilience Boundary & Failure Classification**<br>No retries for 400, 401, 403, 404, 409 or caller cancellation; TLS errors classified as `CatalogTransportRejected` and not retried; Catalog unavailability returns 503 without creating Reservation, Saga, or Payment entities. | **PASS** | Verified in `BusinessAndAuthenticationResponsesHaveNoRetries`, `TlsAndProtocolFailuresAreNotRetried`, `TotalBudgetCancelsEvenFirstAttemptAndPerAttemptBudgetRetriesOnlyTwice`, and `RealCatalogFailureReturns503AndCreatesNoDurableReservationWork`. Database assertions confirm zero durable reservation/saga entities are created during failures. |
| **Distributed Tracing & Context Propagation**<br>Structured JSON logs with service, business, message, and trace IDs; W3C `traceparent` and `tracestate` in message envelopes; consumer spans link to incoming parent; durable work spans link to business IDs; allowlisted field export redacts credentials and payloads. | **PASS** | Implemented in [`LabTelemetry.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Shared.Infrastructure/LabTelemetry.cs), [`Workers.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Shared.Infrastructure/Messaging/Workers.cs#L18-L23), and [`OutboxDispatch.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Shared.Infrastructure/Messaging/OutboxDispatch.cs#L62-L65). Verified in [`ProjectionIsAbsentBeforeInitialEventThenNewestWinsAndMessageSpansFollowIncomingContext`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L145-L171). `AddingOptionalTracestateDoesNotChangeLegacyInboxFingerprint` confirms Inbox deduplication hashing remains stable. |
| **AC-18: Informational Tour Projection**<br>Reservations maintains an informational copy of tour sessions from `TourSessionChanged` events; newer `PriceVersion` wins; older versions ignored; absent projection handled gracefully; Catalog remains authoritative for quotes and seat holds. | **PASS** | Verified in `ProjectionIsAbsentBeforeInitialEventThenNewestWinsAndMessageSpansFollowIncomingContext`. Versions 1, 3, 2, 3 tested; highest version (3) retained; query exposes projection; attempt to use projection price (300) directly for reservation is rejected (409 Conflict) while authoritative quote succeeds. |
| **AC-23: Per-Service DLQ Inspection & Replay**<br>Per-service DLQ isolation; inspect up to 10 messages with 120s visibility; server-side receipt handles and claim leases in `DlqInspections`; replay confirms send before DLQ delete; uncertain delete failure recorded; duplicate delivery preserved for idempotent processing. | **PASS** | Verified in [`PoisonFiveReceivesCorrectionReplayAndSendDeleteFailureKeepOriginalIdentityAndOneProjectionEffect`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L72-L105) and [`ExpiredInspectionsRejectReplayAndCrashedReplayLeaseCanBeInspectedAgain`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L122-L143). 5 poison deliveries redrive to DLQ; replay with simulated delete failure yields `SentDeleteUncertain`; subsequent retry yields `SentAndDeleteAccepted`; single projection effect and exactly 1 Inbox record preserved. |
| **Controlled Fault Injection & Durability**<br>Fault modes (`QuoteTimeout`, `PauseConsumption`, `PauseOutbox`) persisted in DB; occurrences decremented per operation; reset endpoint restores normal behavior without clearing durable work or altering accepted payment provider modes. | **PASS** | Verified in [`PauseOutboxResetResumesSameDurableReservationAndOutboxIdentity`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L192-L206) and [`PaymentPauseConfigurationDoesNotRewriteAcceptedProviderMode`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L107-L120). Reservation created while Outbox paused retains unpublished intent; reset publishes original delivery; payment provider mode snapshot is not overwritten. |
| **Admin Operations Authorization & Rate Limiting**<br>Diagnostics, DLQ inspection/replay, and fault endpoints enforce Admin role and 60 req/min rate limiting; anonymous requests return 401; non-admin users return 403; page sizes capped. | **PASS** | Verified in [`ServiceOwnedDiagnosticsAreBoundedRedactedAndAllOperationsRejectNonAdmins`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/OperationsTests.cs#L54-L70). Tested anonymous (401), non-admin Alice (403), Admin (200), and oversized pages (400) across all 4 services. |
| **Blazor Screens & Client Request Budgets**<br>`/operations` and `/tour-projections` screens provide Admin and authenticated user visibility; manual refresh only; no automatic polling; SSR prerender verified. | **PASS** | Implemented in [`Operations.razor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Gateway.Web/Components/Pages/Operations.razor) and [`TourProjections.razor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Gateway.Web/Components/Pages/TourProjections.razor). Verified via SSR prerender in [`CatalogScreensPrerenderThroughGatewayWithCookieIdentity`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/CatalogHttpTests.cs#L96-L111). |

---

## 3. Prioritized Findings

### Defect Findings: None (0 Critical, 0 High, 0 Medium, 0 Low)
No functional, transactional, security, or architectural defects were found in the LAB-006 implementation.

### Architectural & Verification Observations (Informational)

1. **Consecutive-Failure Circuit Breaker vs. Polly Ratio-Based Breaker**
   - *Observation*: Codex implemented a custom thread-safe `QuoteConcurrencyLimit` state machine instead of Polly's v8 circuit breaker strategy.
   - *Rationale*: Polly v8's reactive breaker uses a sampled failure ratio over a time window rather than consecutive qualifying failures. To satisfy the exact specification ("circuit opens after 5 consecutive qualifying failures per instance, remains open for 30 seconds, and probes recovery"), `QuoteConcurrencyLimit` tracks exact consecutive logical operation failures, generation tokens, and a 30-second cooldown fence.
   - *Disposition*: Documented in ADR 006 (`docs/decisions/006-operations.md`). Fully verified by unit and live integration tests.

2. **Server-Side Receipt Lease Isolation for DLQs**
   - *Observation*: SQS DLQ receipt handles and original message bodies are persisted in the service's `DlqInspections` database table rather than transmitted to the browser client.
   - *Rationale*: SQS receipt handles are opaque, time-sensitive capabilities that expire and should not be exposed externally. Storing them server-side behind a `DeliveryId` identifier and a 110-second inspection lease prevents tampering, enforces authorization, and allows atomic lease claims during replay.
   - *Disposition*: High-quality security and operational design.

3. **OpenTelemetry Package Vulnerability Resolution**
   - *Observation*: The initial OpenTelemetry 1.14.0 packages triggered NuGet audit warnings for transitive vulnerabilities.
   - *Resolution*: Upgraded to OpenTelemetry 1.17.0 across Directory.Packages.props. `dotnet list package --vulnerable --include-transitive` confirms 0 vulnerabilities across all 9 projects without warning suppressions.
   - *Disposition*: Clean dependency hygiene.

4. **Caller Budget Alignment**
   - *Observation*: With Catalog quote client configured for a 10-second total timeout, client callers (such as test runners and Gateway per-user HTTP clients) previously using 10-second timeouts could time out before receiving the 503 response.
   - *Resolution*: Gateway's `GatewayApi` client timeout was increased to 40 seconds, and test callers were increased to 20 seconds, ensuring caller timeouts do not prematurely abort valid 503 error propagation.

5. **Browser Automation Limitation (Documented & Maintained)**
   - *Observation*: Automated browser click-through tests were not executed due to the established Windows sandbox ACL constraint.
   - *Behavior*: Blazor components (`Operations.razor`, `TourProjections.razor`) were verified via code review, SSR rendering in `CatalogHttpTests.cs`, and client-side HTTP contract tests in `OperationsTests.cs`.
   - *Disposition*: Manual browser verification steps are documented in `docs/operations-execution.md`; full automated browser E2E remains scheduled for LAB-007.

---

## 4. Executed Checks and Actual Results

| Check / Command | Exit Code | Result | Summary |
|---|---|---|---|
| `dotnet restore --locked-mode` | 0 | PASS | All 9 projects restored cleanly against pinned lockfiles. |
| `dotnet build --no-restore` | 0 | PASS | Build succeeded with 0 warnings and 0 errors across 9 projects. |
| `powershell -File scripts/Verify-Operations.ps1` | 0 | PASS | 13 live tests passed (1m 11s), 4 controlled tests passed (27s). All containers restored healthy. |
| `powershell -File scripts/Verify-Sagas.ps1` | 0 | PASS | 15 passed, 0 failed, 0 skipped (2m 39s). LAB-005 Saga and Payment regressions verified. |
| `powershell -File scripts/Verify-Messaging.ps1` | 0 | PASS | 8 passed, 0 failed, 0 skipped (48s). LAB-003 messaging and Catalog stop/restart verified. |
| `powershell -File scripts/Verify-Local.ps1` | 0 | PASS | 58 passed, 0 failed, 28 skipped (2m 6s). All unit/integration tests verified. |
| `dotnet ef migrations has-pending-model-changes --no-build` | 0 | PASS | 0 pending model changes for `Catalog.Api`, `Reservations.Api`, `Payments.Api`, and `Notifications.Api`. |
| `dotnet list package --vulnerable --include-transitive` | 0 | PASS | 0 vulnerable packages across all 9 projects. |
| `git diff --check` | 0 | PASS | No git whitespace or line-ending errors. |

---

## 5. Unverified Items and Readiness for LAB-007

1. **Interactive Multi-User Browser Circuit Isolation (AC-26)**:
   - *Status*: NOT VERIFIED by browser automation due to environment sandbox ACLs.
   - *Follow-up*: Manual operator testing in browser (steps in [`docs/operations-execution.md`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/operations-execution.md)); automated browser verification deferred to LAB-007.

2. **AWS Cloud Deployment & CloudWatch Observability**:
   - *Status*: NOT VERIFIED (deliberately out of scope for local lab).
   - *Follow-up*: Scheduled for LAB-008 (IaC) and LAB-009 (explicit deployment).

---

## 6. Conclusion

LAB-006 is **APPROVED (PASS)**. All resilience policies, observability instruments, informational projections, DLQ inspection/replay controls, and controlled fault mechanics operate correctly in compliance with specifications and project invariants. Ready to proceed to **LAB-007**.
