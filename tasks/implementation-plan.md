# Task Plan

Work on one task at a time. Maintain this file with actual status and evidence. Do not automatically mark learning topics as mastered. Build incrementally: introduce a minimum UI early so the system is visible before completing fault injection.

## Actual status — October 3, 2026

LAB-001 is complete and fully verified. Local build, five image builds, Identity login, database permissions,
TLS, encrypted key persistence, stop/start persistence, and operator manual browser testing were verified
(see `docs/evidence/LAB-001.md` and `docs/evidence/LAB-001-review.md`).
- Manual browser smoke testing (TLS trust, Alice login, interactive Account circuit, access denial on `/admin`, CSRF logout & cookie deletion, Admin login) was confirmed by the operator.
- Automated multi-user circuit isolation (AC-26) and browser UI E2E testing remain scheduled for LAB-007.
- Gateway rate limiter client partitioning is non-blocking for LAB-001 and deferred to LAB-007/008 (conditional on upstream ingress/ALB topology).
LAB-002..009 remain pending. No learning topic is marked mastered; no AWS resources were deployed.

| ID | Work | Depends on | Exit criterion |
|---|---|---|---|
| LAB-001 | .NET10 scaffold, PostgreSQL/ElasticMQ Compose, schema/role bootstrap, local HTTPS, Identity users, login/layout UI | — | Completed & verified: build, login, tested DB permissions, base images, manual smoke test |
| LAB-002 | Catalog CRUD/sessions, quotes, atomic inventory, contracts | 001 | API and AC-04/17; basic catalog/admin UI |
| LAB-003 | Idempotent Reservations creation and state/timeline; Outbox/Inbox/SQS adapter without shared domain | 002 | AC-03/07/08/09 and quote 503; reservations UI |
| LAB-004 | Durable fake Payments, operations/reconciliation; successful hold→pay→confirm Saga; fake Notifications | 003 | AC-02/05/10/24; payments/notifications UI |
| LAB-005 | Saga compensations, expiration, cancellation, late results, concurrency | 004 | AC-06/11/12/13/14/15/16/22 |
| LAB-006 | Resilience, observability, projection, DLQ, admin/fault controls | 005 | AC-18/19/23; complete admin UI |
| LAB-007 | Auth/ownership hardening, Blazor scopes, UI E2E, learning map | 006 | AC-20/21/25/26/27/28/29; documentation; browser UI E2E & circuit isolation; rate-limiter trusted forwarded headers if reverse proxy introduced |
| LAB-008 | Fargate IaC, external secrets, one-shot migrations, preflight/start/stop/destroy | At least 004; ideally 007 | terraform validate/reviewable plan and cost estimate; NO automatic deployment; ingress topology & trusted proxy configuration |
| LAB-009 | Explicit AWS deployment/smoke tests, pause/start, interview rehearsal | 008 | Actual AC-30 evidence and demo |

LAB-004 delivers a functioning vertical slice. LAB-005..007 complete resilience. Do not confuse a deployable prototype with verification of every pattern. IaC may be generated alongside local work, but agent delegation is neither required nor instructed.

## Dependency versions
Pin an available .NET10 SDK in global.json and compatible packages/images after verifying official sources. Avoid floating latest versions. Use compatible EF10/Npgsql10, YARP, AWS SDK, OpenTelemetry, and xUnit versions. MediatR is optional; do not require a paid license/package. Prefer a simple explicit dispatcher. No TODO placeholders in a workflow declared implemented.

## Each Codex delivery
State the problem resolved, observable behavior, relevant files, executed tests/checks and actual results, limitations, and next task. Add an ADR when changing a decision. Update docs/learning-map with existing examples. Never say "works in AWS" merely because Dockerfiles or Terraform exist.
