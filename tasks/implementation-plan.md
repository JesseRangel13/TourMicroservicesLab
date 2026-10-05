# Task Plan

Work on one task at a time. Maintain this file with actual status and evidence. Do not automatically mark learning topics as mastered. Build incrementally: introduce a minimum UI early so the system is visible before completing fault injection.

## Actual status — October 4, 2026

LAB-001 is complete and fully verified. Local build, five image builds, Identity login, database permissions,
TLS, encrypted key persistence, stop/start persistence, and operator manual browser testing were verified
(see `docs/evidence/LAB-001.md` and `docs/evidence/LAB-001-review.md`).
- Manual browser smoke testing (TLS trust, Alice login, interactive Account circuit, access denial on `/admin`, CSRF logout & cookie deletion, Admin login) was confirmed by the operator.
- Automated multi-user circuit isolation (AC-26) and browser UI E2E testing remain scheduled for LAB-007.
- Gateway rate limiter client partitioning is non-blocking for LAB-001 and deferred to LAB-007/008 (conditional on upstream ingress/ALB topology).
LAB-002 is complete and verified: Catalog migration/seed, CRUD/session/quote APIs, atomic inventory
handlers, persisted Outbox intents and Catalog/Admin screens. Build, real PostgreSQL/HTTPS,
contract, SSR, and stop/start durability checks pass. Independent review verdict is PASS (docs/evidence/LAB-002-review.md).
- Manual browser testing (tour search/detail, admin CRUD/sessions, inactive tour filtering, VersionConflict handling) was confirmed by the operator.
- AC-04 and AC-17 are verified at the Catalog layer; full end-to-end multi-service saga checks begin in LAB-003.
- Automated multi-user circuit isolation (AC-26) remains scheduled for LAB-007.
LAB-003 is complete and verified: owned reservation creation/queries/history,
service-authenticated quotes, HTTP idempotency, leased Outbox, transactional Inbox, real SQS SDK
transport, live hold/rejection workflow and Blazor reservation screens. Local suite: 45 passed;
controlled messaging suite: 8 passed; extended stop/start test: 1 passed. Independent review verdict is PASS (docs/evidence/LAB-003-review.md).
- Workflow stops at AwaitingPayment or Failed. Payments/Notifications consumers remain disabled.
- Manual browser smoke testing (Alice reservation creation, AwaitingPayment transition, retry/intention idempotency, Bob 404 ownership isolation, and 1-seat final competition between Alice and Bob) was confirmed by the operator.
- AC-03/04/07/08/09 have the scoped automated evidence documented in LAB-003; full payment confirmation, compensation, and payment success arrive in LAB-004 and LAB-005.
- Automated multi-user circuit isolation (AC-26) remains scheduled for LAB-007.
LAB-004 implements independently durable simulated payment/provider and notification/sender work,
reconciliation, success through Catalog confirmation and decline through release confirmation.
- LAB-004 automated evidence and review boundaries are recorded in docs/evidence/LAB-004.md.
- Final verification: 45 normal tests, 5 controlled payment/sender workflow tests, 8 messaging
  regression tests and 1 corrected stop/start Pending-work recovery test passed; build has zero warnings/errors.
- Independent review verdict is PASS (docs/evidence/LAB-004-review.md). Automated AC-02, AC-05, AC-10, AC-24,
  charged-confirmation compensation safety prerequisite, and restart durability are fully verified.
- Interactive multi-user browser testing remains pending operator manual verification / LAB-007.
- Refund/release intents and flags after charged confirmation rejection are a documented minimum
LAB-005 implements durable full refunds, cancellation, persisted deadlines, uncertainty/late-result repair and protected Admin recovery. Local verification passed: 45 normal tests, 15 controlled payment/recovery tests, 8 messaging regressions, 1 actual stop/start recovery test and 4 final focused recovery checks; five images built, migrations match their models. Independent review verdict is PASS (docs/evidence/LAB-005-review.md). Automated AC-06, AC-11, AC-12, AC-13, AC-14, AC-15, AC-16, AC-22, dual compensation, and restart recovery are fully verified. Interactive LAB-005 browser checks remain pending operator verification / LAB-007. See docs/evidence/LAB-005.md and docs/evidence/LAB-005-review.md. LAB-006 supplies bounded quote resilience, local OpenTelemetry/JSON correlation, owned diagnostics/DLQ replay, audited faults and the informational projection. Local checks: 58 normal tests, 13 live resilience/operations tests, 4 controlled operations tests, 15 Saga regressions and 8 messaging regressions passed; optional Jaeger export was inspected. Independent review verdict is PASS (docs/evidence/LAB-006-review.md). Interactive checks remain LAB-007. See docs/evidence/LAB-006.md and docs/evidence/LAB-006-review.md. LAB-007 implements local auth/ownership hardening, corrected Interactive Server operations/projection controls, client lifetime checks, browser-test artifacts and the final learning/evidence map. Actual results are in docs/evidence/LAB-007.md; the DEFECT-LAB007-01 follow-up passed both real browser tests; broader AC-27/28/29 gaps remain documented. LAB-008/009 remain pending. No learning topic is marked mastered;
no AWS resources were deployed.

LAB-007 final actual local checks: **66 normal passed / 29 intentional skips**, **26 focused security passed / 1 intentional limiter skip**, **15 Saga passed**, **1 actual encrypted-cookie/owned-work restart passed**, and **1 dedicated limiter check passed**; zero final failures. Locked restore/build has zero warnings/errors, five images built and hosts restored healthy, package audit clean. Initial browser-tool reassessment failed; subsequent CLI Playwright execution resolved DEFECT-LAB007-01 and passed both pinned tests in 45.4 seconds (docs/evidence/DEFECT-LAB007-01.md). Local code/artifacts are ready for independent review with remaining browser expiration, comprehensive failure/recovery and navigation-cancellation gaps explicitly open. See `docs/evidence/LAB-007.md`, `docs/acceptance-matrix.md`, `docs/security-execution.md`, and `docs/demo-guide.md`. LAB-008 prerequisites include independent review, browser evidence, fresh cost/quota checks, Neon role/TLS compatibility, IAM/external secrets, one-shot migrations and actual limited-session ingress/circuit decisions; infrastructure artifacts only, no deployment.

| ID | Work | Depends on | Exit criterion |
|---|---|---|---|
| LAB-001 | .NET10 scaffold, PostgreSQL/ElasticMQ Compose, schema/role bootstrap, local HTTPS, Identity users, login/layout UI | — | Completed & verified: build, login, tested DB permissions, base images, manual smoke test |
| LAB-002 | Catalog CRUD/sessions, quotes, atomic inventory, contracts | 001 | Completed & verified: CRUD, quotes, atomic inventory, Outbox intents, SSR & manual UI checks |
| LAB-003 | Idempotent Reservations creation and state/timeline; Outbox/Inbox/SQS adapter without shared domain | 002 | Completed & verified: automated AC-03/04/07/08/09, quote 503, outbox/inbox SQS, manual UI checks |
| LAB-004 | Durable fake Payments, operations/reconciliation; successful hold→pay→confirm Saga; fake Notifications | 003 | Completed & verified: automated AC-02/05/10/24 evidence, payment/notification UI built, review verdict PASS |
| LAB-005 | Saga compensations, expiration, cancellation, late results, concurrency | 004 | Completed & verified: automated AC-06/11/12/13/14/15/16/22 evidence, review verdict PASS |
| LAB-006 | Resilience, observability, projection, DLQ, admin/fault controls | 005 | Completed & verified: automated AC-18/19/23, owned Admin controls, quote resilience, DLQ replay; review verdict PASS |
| LAB-007 | Auth/ownership hardening, Blazor scopes, UI E2E, learning map | 006 | Independent review verdict: INCOMPLETE VERIFICATION (docs/evidence/LAB-007-review.md). Backend APIs, security tokens, role/DB isolation, and form/cookie tests PASS. Historical review browser run: 1 passed / 1 failed. DEFECT-LAB007-01 subsequently resolved; both real browser tests passed in 45.4 seconds (docs/evidence/DEFECT-LAB007-01.md). Focused live-circuit isolation and controls verified; full expiration/recovery/navigation evidence remains open. Independent review verdict preserved. |
| LAB-008 | Fargate IaC, external secrets, one-shot migrations, preflight/start/stop/destroy | At least 004; ideally 007 | terraform validate/reviewable plan and cost estimate; NO automatic deployment; ingress topology & trusted proxy configuration |
| LAB-009 | Explicit AWS deployment/smoke tests, pause/start, interview rehearsal | 008 | Actual AC-30 evidence and demo |

LAB-004 delivers a functioning vertical slice. LAB-005..007 complete resilience. Do not confuse a deployable prototype with verification of every pattern. IaC may be generated alongside local work, but agent delegation is neither required nor instructed.

## Dependency versions
Pin an available .NET10 SDK in global.json and compatible packages/images after verifying official sources. Avoid floating latest versions. Use compatible EF10/Npgsql10, YARP, AWS SDK, OpenTelemetry, and xUnit versions. MediatR is optional; do not require a paid license/package. Prefer a simple explicit dispatcher. No TODO placeholders in a workflow declared implemented.

## Each Codex delivery
State the problem resolved, observable behavior, relevant files, executed tests/checks and actual results, limitations, and next task. Add an ADR when changing a decision. Update docs/learning-map with existing examples. Never say "works in AWS" merely because Dockerfiles or Terraform exist.
