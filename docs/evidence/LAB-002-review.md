# LAB-002 Independent Review Report

**Date**: October 3, 2026  
**Subject**: TourMicroservicesLab — LAB-002 Catalog, Sessions, Quotes, Atomic Inventory & UI  
**Target Solution**: `TourMicroservicesLab.slnx` (.NET 10.0.401, PostgreSQL 17.6, ElasticMQ 1.6.15)  

---

## 1. Verdict

### **PASS**

Codex's implementation of **LAB-002** satisfies all functional, architectural, transactional, and security requirements specified in the design and task documents. Real PostgreSQL isolation, transactional consistency, cross-process concurrency controls, service-to-service RS256 authentication, Outbox intent generation, and Blazor server-rendered UI were independently verified.

Operator manual browser verification of all new interactive features (tour search/filtering, tour and session creation, price and capacity updates, inactive tour visibility filtering, and optimistic concurrency version conflict detection) was successfully confirmed.

---

## 2. Acceptance Matrix

| Requirement / Acceptance Criterion | Status | Evidence / Verification Notes |
|---|---|---|
| **AC-04 (Catalog Handler Portion): Atomic Inventory Competition** | **PASS** | `ConcurrentFinalSeatHasExactlyOneWinner` in `CatalogTests.cs`: two concurrent hold attempts for the final seat result in exactly one winner and one `InsufficientSeats` rejection; `AvailableSeats` never drops below zero. Handled via conditional SQL in `InventoryHandlers.cs` and check constraint `CK_Session` in `CatalogDb.cs`. |
| **AC-17 (Catalog Portion): Quote Immutability & Price Changes** | **PASS** | `ValidQuoteKeepsItsPriceAfterPriceChangesAndCreatesTransactionalProjectionIntent` and `UnacceptedAmountOrCurrencyCannotCreateQuote` in `CatalogTests.cs`. Quotes lock the accepted unit price for 5 minutes; unaccepted price/currency rejects with 409 `PriceChanged`. |
| **AC-13 (Inventory Portion): Confirmation vs. Expiration Serialization** | **PASS** | `ConfirmationAndExpirationSerializeAtTheDeadline` and `ConfirmationBeforeDeadlineWinsAndCanBeReleasedOnce` in `CatalogTests.cs`. Confirmations at/past deadline serialize with expiration; confirmation before deadline wins; seats restored exactly once. Handled via `ConfirmAsync`, `ExpireAsync`, and `ReleaseAsync`. |
| **AC-16 (Partial): Outbox Idempotency & Operation Keys** | **PASS** | `DuplicateHoldAndReleaseHaveOneInventoryEffectAndOneIntentPerTransition` in `CatalogTests.cs`. Replays and duplicate calls produce exactly one business effect and reuse the persisted Outbox effect key without duplicating dispatch rows. Unique constraint `(Destination, EffectKey)` enforced on `catalog.Outbox`. |
| **W-06: Out-of-Order Release (Release-Before-Hold)** | **PASS** | `ReleaseBeforeHoldPersistsTombstoneAndPreventsLateDeduction` in `CatalogTests.cs`. Release arriving before Hold creates a persistent tombstone (`Status = "Released"`), causing any late Hold command to reject without seat deduction. |
| **W-06: Rejected Hold Stability** | **PASS** | `RejectedHoldDoesNotBecomeSuccessfulAfterCapacityIncreases` in `CatalogTests.cs`. A hold rejected for insufficient seats or invalid quote persists its rejection and cannot later become successful if capacity increases. |
| **Capacity & Optimistic Concurrency Invariants** | **PASS** | `CapacityCannotDropBelowHeldOrConfirmedSeatsAndVersionsPreventLostUpdates` in `CatalogTests.cs`. Capacity cannot drop below occupied seats. Conflicting concurrent edits are rejected with 409 `VersionConflict`. |
| **Catalog Human CRUD & Admin Authorization** | **PASS** | `GatewayCatalogCrudPaginationAndProblemDetailsUseAuthenticatedAdmin` and `TouristCannotMutateCatalogAndNoPublicInventoryRoutesExist` in `CatalogHttpTests.cs`. Listing, search, and details require authentication; mutation routes require `Admin` role; non-admins receive 403 `Forbidden`. |
| **AC-21 (Service Auth Portion): Dedicated Service Identity for Quotes** | **PASS** | `InternalQuotesRequireDedicatedReservationsIdentityAndAreBlockedByGateway` in `CatalogHttpTests.cs`. `POST /v1/internal/quotes` requires an RS256 token signed by the Reservations key (`sub: reservations`, `permission: quote:create`, `aud: catalog-internal`, `iss: tourlab-services`, validity <= 2 min). Gateway blocks internal routes with 404. |
| **AC-25: Schema & Privilege Isolation** | **PASS** | `BootstrapIsIdempotentAndCatalogRuntimeCannotReadMigrationHistory` in `CatalogTests.cs`. Runtime role `catalog_runtime` has DML only in schema `catalog`, has no access to `__EFMigrationsHistory`, and cannot query other schemas. DDL owned strictly by `catalog_migrator`. |
| **AC-28 (UI & Operator Verification)** | **PASS** | Server-side rendering (SSR) verified by `CatalogScreensPrerenderThroughGatewayWithCookieIdentity` in `CatalogHttpTests.cs`. Interactive controls, active/inactive filtering, and optimistic concurrency version conflict alert were manually tested and confirmed by the human operator. |
| **State Durability across Docker Restart** | **PASS** | `DatabaseQueueMessageAndLoginCookieSurviveLocalStopStart` in `PersistenceTests.cs` executed via `scripts/Verify-Restart.ps1`. MD5 hashes of all 5 Catalog tables (`Tours`, `Sessions`, `Quotes`, `Holds`, `Outbox`), Identity users, active session cookies, and ElasticMQ queues survive Docker stop/start. |

---

## 3. Findings and Observations

### Defect Findings: None (0 Critical, 0 High, 0 Medium, 0 Low)
No functional, security, or architectural defects were identified in the LAB-002 implementation.

### Observations (Informational / Deferred)

1. **Outbox Message Dispatching Deferred to LAB-003 (Expected Scope Boundary)**
   - Outbox records (`catalog.Outbox`) are created transactionally with immutable JSON payloads, correlation IDs, and unique effect keys, but `PublishedAtUtc` remains `null`. No worker thread, SQS sender, or polling mechanism is present in `Catalog.Api`.
   - *Assessment*: Matches `tasks/implementation-plan.md` scope; dispatching and transport are scheduled for LAB-003.

2. **Automated Headless Browser Testing Limitation (Carried Over from LAB-001)**
   - Automated browser execution remains blocked in this environment due to the Windows sandbox ACL initialization error (`windows sandbox failed: helper_unknown_error: apply deny-read ACLs`).
   - *Assessment*: Covered by automated HTTP/SSR integration tests and operator manual verification. Multi-user circuit isolation and E2E browser tests remain scheduled for LAB-007.

3. **Gateway Forwarded Headers / Rate Limiter Partitioning (Carried Over from LAB-001)**
   - Fixed-window rate limiter partitions by `RemoteIpAddress`.
   - *Assessment*: Non-blocking for local development; deferred to LAB-007/LAB-008 when upstream ALB/reverse proxy ingress is configured.

---

## 4. Executed Checks Summary

| Command | Scope | Outcome |
|---|---|---|
| `git status -s` | Workspace hygiene | Verified expected LAB-002 file changes. |
| `git diff` | Core files & configuration | Verified no regressions in scaffold or security pipeline. |
| `docker compose ps` | Container health | All 7 containers (`postgres`, `elasticmq`, `gateway`, `catalog`, `reservations`, `payments`, `notifications`) healthy. |
| `powershell -File ./scripts/Verify-Local.ps1` | Full local test suite | **38 passed, 0 failed, 1 skipped** (50.5s total runtime). |
| `powershell -File ./scripts/Verify-Restart.ps1` | Cold Docker restart durability | **1 passed, 0 failed, 0 skipped** (23.9s total runtime; zero data loss across restart). |
| Manual Operator Verification | Browser UI interactive controls | All 3 test scenarios (Tourist search/detail, Admin tour/session CRUD & deactivation, VersionConflict detection) confirmed. |

---

## 5. Unverified Items & Boundaries (Scheduled for Later Labs)

- **Full Reservation Saga & Two-User End-to-End Saga (AC-02, AC-04, AC-11, AC-13 Saga portion)**: Begins in LAB-003 and completes in LAB-004/005.
- **Outbox Leased Dispatching & SQS Delivery (AC-07, AC-08, AC-09)**: Scheduled for LAB-003.
- **Projection Consumption (AC-18)**: Scheduled for LAB-003 / LAB-006.
- **Circuit Multi-User Isolation & E2E Browser Testing (AC-26)**: Scheduled for LAB-007.
- **AWS Deployment & Remote Verification (AC-30)**: Scheduled for LAB-008 and LAB-009.

---

## 6. Handoff to Codex (Prioritized Next Steps for LAB-003)

Codex may proceed to **LAB-003**:

1. **Idempotent Reservations Service & Workflow (`Reservations.Api`)**:
   - Implement `POST /v1/reservations` requiring mandatory `Idempotency-Key` header with body `(sessionId, participants, expectedUnitAmountMinor, expectedCurrency)`.
   - Issue internal RS256 service token from Reservations to call `Catalog /v1/internal/quotes`. Return 503 if Catalog is unreachable without fabricating a saga or charge.
   - Persist reservation, initial saga state (`AwaitingAvailability`), and `HoldSeats` command into `reservations.Outbox` in a single local database transaction.
2. **Outbox / SQS Adapter & Reliable Dispatcher**:
   - Implement leased claiming (`SKIP LOCKED` or atomic lease updates) for outbox tables in Catalog and Reservations.
   - Dispatch integration envelopes over local ElasticMQ SQS queues without ACK-before-commit.
3. **Inbox Pattern & Consumer Deduplication**:
   - Implement consumer Inbox (`unique(ConsumerName, MessageId)`) for incoming messages (e.g. Catalog consuming `HoldSeats`, Reservations consuming `SeatsHeld` / `SeatsHoldRejected`).
   - Retain deduplication tombstones and records for the required minimum 14-day retention period.
4. **Hold Expiration Background Worker**:
   - Integrate Catalog's `InventoryHandlers.ExpireAsync` with a background timer worker to automatically process expired holds.
5. **Reservations Blazor UI**:
   - Add initial reservations list and detail screens to `Gateway.Web`.
