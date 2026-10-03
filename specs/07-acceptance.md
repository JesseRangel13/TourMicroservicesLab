# 07 — Observable Verification

Integration tests use real PostgreSQL and the local transport, not EF InMemory. Use xUnit for unit/integration/contract tests, HTTP and reproducible scripts for E2E, and Playwright only for useful UI coverage. Tests require neither AWS nor a paid plan. Use a replaceable clock for deadlines instead of ten-minute sleeps. Failures must be deterministic, limited in occurrences, and enabled only with LabFeaturesEnabled.

| ID | Scenario | Observable outcome |
|---|---|---|
| AC-01 | Seed and log in Alice/Bob/Admin | Password hashes, correct roles, no credentials in Git |
| AC-02 | Reserve a valid session | Confirmed, one charge, capacity deducted once, one notification |
| AC-03 | Concurrent double click/HTTP retry | Same reservation and response for the same key; different content=409 |
| AC-04 | Two users compete for the last seat | Only one obtains a hold; AvailableSeats never becomes negative |
| AC-05 | Provider Decline | Failed, no charge, capacity restored once |
| AC-06 | TimeoutAfterCharge | Temporary Unknown→reconciled→Confirmed; exactly one fake charge effect |
| AC-07 | Restart after reservation commit | Pending Outbox resumes; no lost request |
| AC-08 | Restart after Send but before marking sent | Message may repeat; Inbox prevents a second effect |
| AC-09 | Restart after Inbox commit but before ACK | Recognize redelivery without another transition/notification |
| AC-10 | Restart after provider effect but before result | Reconciliation recovers the charge by ID without another charge |
| AC-11 | ConfirmSeats rejected after payment | Refund + release; Failed only after both |
| AC-12 | Cancel a confirmed reservation twice | CancellationPending→Cancelled; one refund and one release |
| AC-13 | Hold expires and PaymentSucceeded arrives late | Compensation; no confirmation without seats or duplicated inventory |
| AC-14 | RefundTransientFailure | Retry using the same refundId; UI remains pending until completion |
| AC-15 | Unknown never resolves | ManualReview; do not claim no charge; Admin resolves provider and reconciles |
| AC-16 | Duplicates with different MessageIds | Operation IDs and state protect the business effect |
| AC-17 | Price changes | Preserve a valid quote; unaccepted price=409; no charge |
| AC-18 | Out-of-order projection updates | Preserve latest PriceVersion; never authorize capacity from the projection |
| AC-19 | Slow/unavailable Catalog | Bounded total timeout, breaker opens/recovers; 503 without payment |
| AC-20 | Alice accesses Bob's reservation/payment/notification | 404; authorized Admin may inspect |
| AC-21 | Invalid token/signature/issuer/audience/expiration | 401 at the service even when bypassing Gateway |
| AC-22 | Two replicas update one Saga | Detect Version conflict without overwriting or duplicating events |
| AC-23 | Incompatible/poison message | DLQ after the limit; replay preserves ID and recovers after correction |
| AC-24 | Sender fails after message consumption | Durable Pending work keeps retrying; one fake delivery |
| AC-25 | Runtime credentials query another schema | PostgreSQL rejects the query |
| AC-26 | Two simultaneous Blazor users | No JWT/state/list leakage across circuits |
| AC-27 | Login/logout/cookies/forged forms | Antiforgery, sign-out, and expiration work; client cannot choose a role |
| AC-28 | Basic UI | Every human contract action is available and errors are visible |
| AC-29 | Disposal/cancellation | Worker scope per operation, no concurrent context sharing, safe shutdown |
| AC-30 | Cloud pause/start | Data/queues survive; zero tasks after stop; changed IP documented |

Fault hooks for AC-07..10 are lab-only, using controllable test barriers or kill scripts at deterministic points. Do not add public crash endpoints. Configure confirmation failure in Catalog; do not fabricate PaymentSucceeded to pass tests.
Tests do not promise exactly-once network delivery: they verify one business effect in the fictional provider under its implemented contract.

## Definition of Done by layer
Local MVP: login, real data, UI, AC-02/03/04/05/12/20/25/28, and builds of five images.
Robust distributed workflow: relevant AC-06..24 plus 26/27/29, with evidence of retries, duplicates, and restarts.
AWS: AC-30 and Blazor smoke tests for AC-02/12; verified IAM/network/TLS, updated estimates, documented stop/destroy. Do not claim complete resilience after testing only the happy path.

## Required evidence
One file per task with executed commands, actual results, UI screenshots where appropriate, and limitations. Requirement→test→implementation-path mapping. A 10–15-minute demo guide covering success, double submission, decline, timeout after charge, compensation, and correlation. Five interview answers in English with supporting concept explanations in English.
