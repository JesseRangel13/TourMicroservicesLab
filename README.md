# TourMicroservicesLab

LAB-001 implements the local foundation: five .NET 10 hosts, PostgreSQL schema isolation,
durable ElasticMQ storage, HTTPS, Identity, and a basic Blazor Interactive Server account screen.
LAB-002 adds Catalog tours/sessions, service-only quotes, transactional inventory handlers,
durable publication intents, and basic catalog/admin screens. LAB-003 adds durable Reservations and
SQS Outbox/Inbox. LAB-004 adds independently durable simulated provider/sender effects, payment
reconciliation, successful confirmation, declined-payment release and payment/notification screens.
LAB-005 adds durable refunds, cancellation, persisted Saga deadlines, late-result compensation and authorized recovery controls.
LAB-006 adds bounded quote resilience, JSON/OpenTelemetry correlation, service-owned diagnostics/DLQ replay, durable fault controls and the informational tour projection.
The specifications below and in `specs/` remain the source of truth.

## Run locally on Windows

Prerequisites: PowerShell 7, .NET SDK **10.0.401**, and Docker Desktop using Linux containers.
Ports 8443–8447, 54329, and 9324 must be available. All published ports bind only to 127.0.0.1.
Run these commands from the repository root:

```powershell
dotnet tool restore
dotnet restore --locked-mode
./scripts/New-LocalConfiguration.ps1
./scripts/Trust-LocalCa.ps1
./scripts/Start-Local.ps1
./scripts/Verify-Local.ps1
./scripts/Verify-Restart.ps1
```

For subsequent sessions, reuse `.local` and run `./scripts/Start-Local.ps1 -SkipBuild`.
After editing application code, use Start-Local without SkipBuild to rebuild images.
New-LocalConfiguration intentionally refuses to overwrite an existing secret set.
Stop with `./scripts/Stop-Local.ps1`; it preserves PostgreSQL, queues, and messages.
Never use `docker compose down -v` as a pause or test cleanup operation.

Open **https://localhost:8443**. The trusted private CA is for this lab only; a public certificate
or bought domain is unnecessary. Gateway and internal APIs verify TLS certificates.
PostgreSQL uses VerifyFull certificate/hostname verification and rejects plaintext TCP connections.
ElasticMQ's local HTTP endpoint is an explicitly simulated broker exposed on loopback only.

The private `.local/provisioner.json` contains generated passwords for **Alice**, **Bob**, and **Admin**
under `Users`. View it privately in your editor to sign in; do not paste credentials into chat,
logs, shell history, Git, or screenshots. Tourist accounts are Alice/Bob. Admin is the Admin account.
If choosing your own seed passwords, edit the private file **before first bootstrap**. Existing
users/passwords are preserved by subsequent bootstrap; editing a seed is not password rotation.
Login/logout use SSR forms and antiforgery; `/account` demonstrates Interactive Server identity.
Sessions expire after 30 minutes. Signing out invalidates the user's other circuits at their next
revalidation (at most 30 seconds). API JWTs expire after 15 minutes and are stateless until then.

## Local structure and boundaries

| Host | Loopback HTTPS port | Owned PostgreSQL schema |
|---|---|---|
| Gateway.Web (Blazor + Identity + YARP) | 8443 | identity |
| Catalog.Api | 8444 | catalog |
| Reservations.Api | 8445 | reservations |
| Payments.Api | 8446 | payments |
| Notifications.Api | 8447 | notifications |

`Shared.Contracts` contains HTTP DTOs and versioned Catalog integration payloads. `Shared.Infrastructure` contains hosting,
JWT validation, safe errors, and validated connection options. It contains no shared domain entities
or DbContext. `tools/Lab.Provisioner` is a separate local-only one-shot process with privileged
credentials; none are mounted into hosts. Each runtime role can perform DML only in its own schema,
has no CREATE privilege, and has a maximum pool size of five. Each schema has its own migrator role.

The provisioner applies checked-in migrations for all five owned schemas before hosts start
and seeds users and sample tours idempotently. Each business service owns its own Inbox/Outbox,
business tables and runtime credentials. Payments and Notifications also own independent simulated
provider effects and sender receipts committed separately from application results.
Catalog's existing Outbox intents remain intact and now dispatch through real ElasticMQ/SQS.
Data Protection keys persist encrypted in the identity schema using a dedicated private certificate.

`GET /health/live` requires no identity and no database. `GET /health/ready` requires a valid API
JWT and checks only the host's owned connection; it is not polled continuously. Business hosts
expose `/v1/status` (authenticated) and `/v1/admin/status` (Admin only) for scaffold verification.
YARP forwards `/api/{service}/v1/...` over TLS with JWT authentication. All internal paths are blocked.
Catalog exposes quotes only directly to the dedicated Reservations service identity; Gateway
returns 404 for internal paths. No service-token issuing HTTP endpoint exists.

## Checks and debugging

```powershell
dotnet build --no-restore
dotnet test --no-build # pure tests run; local integration/restart tests report SKIP explicitly
./scripts/Verify-Local.ps1 # enables real PostgreSQL and HTTP integration tests
./scripts/Verify-Messaging.ps1 # controlled crash windows; pauses/resumes Catalog/Reservations workers
./scripts/Verify-Payments.ps1 # real transport/provider/sender workflow and independent-effect crash checks
./scripts/Verify-Sagas.ps1 # refund/cancellation/deadline/late-result recovery with real PostgreSQL and ElasticMQ
./scripts/Verify-Restart.ps1 # stops/starts this Compose lab; preserves its data
docker compose ps
curl.exe --ssl-revoke-best-effort --cacert .local/certificates/ca.crt https://localhost:8443/health/live
```

Restart verification retains a uniquely named `tourlab-evidence-*` queue and its message as evidence;
it never purges business queues. It briefly interrupts this local lab, so do not run it during a demo.
Windows curl uses Schannel: `--ssl-revoke-best-effort` tolerates the private CA's absent revocation
endpoint while preserving certificate/hostname verification. Do not use `-k` or `--insecure`.
Failure details and actual check results are in `docs/evidence/LAB-001.md`.
Catalog migration/API/manual UI instructions are in `docs/catalog-execution.md`; LAB-002 results
are in `docs/evidence/LAB-002.md`.
Reservations, queue initialization, staged consumers and manual UI checks are documented in
`docs/reservations-execution.md`; actual LAB-003 results are in `docs/evidence/LAB-003.md`.
LAB-004 consumer instructions are in `docs/payments-execution.md`.
LAB-005 migrations, recovery configuration and manual demos are in `docs/saga-execution.md`.
All disruptive scripts pause/resume all four consumers, payment/refund/notification work and Saga deadline workers.
To regenerate runtime JSON after a path/config-generator change without rotating passwords/keys:
`dotnet run --project tools/Lab.Provisioner -- refresh`.
Use `config/private.example.json` as the secret-free shape reference. Never copy the privileged
provisioner configuration into a runtime host.

For debugging a host outside Docker, first stop that host's container so its port is available,
set `LAB_CONFIG` to the absolute `.local/{service}.host.json` path, and run `dotnet run --no-launch-profile
--project src/{Project}`. Trust the CA first. `.local` and private key files are excluded from both
Git and Docker build contexts. Linux/macOS setup scripts are not provided in LAB-001.

## Scope

No AWS resources, paid services, real card payments, or SMTP deliveries were created.
Neon/Fargate deployment is reserved for LAB-008/009 with fresh quota, cost, and permission checks.
Full diagnostics, browser circuit isolation and later hardening checks remain scheduled tasks.
The implemented workflow is create → hold → simulated payment → actual Catalog confirmation →
Confirmed → simulated notification. A decline stays Compensating until Catalog confirms release,
then becomes Failed. Unknown is reconciled with the original provider operation; it never means decline.
Charged confirmation rejection and cancellation track durable refund/release requirements independently. Persisted deadlines abandon unconfirmed work safely; late charge results initiate refunds. Unresolved payment or compensation enters ManualReview with protected reconciliation/retry controls.

Next task: **LAB-006**, resilience, observability and diagnostics/DLQ controls.

## Original specification overview

Version 1.1 — October 2, 2026. Jesse's student project. English edition.

## Objective
Build a small tour application using .NET 10, a Blazor frontend, four microservices, real PostgreSQL, and a temporary deployment on AWS ECS/Fargate. Payments and email are simulated, with durable state. The priority is to learn distributed-system decisions and demonstrate them in an interview, without turning this lab into a commercial platform.

The original package contained specifications only. This repository now includes LAB-001 through
LAB-006; independent LAB-006 review, LAB-007 security/browser checks and AWS resources remain later work.

## Main decisions
- Backend: .NET 10 / ASP.NET Core, EF Core 10, and a compatible Npgsql provider.
- Frontend: Blazor Web App with Interactive Server, basic HTML and tables. It runs in the same process as the YARP Gateway, reducing the deployment to five ECS services.
- Business services: Catalog (including availability), Reservations (including the Saga), Payments, and Notifications.
- Local PostgreSQL in Docker; Neon Free PostgreSQL for AWS deployment, subject to quotas.
- One physical database with isolated schemas and roles per service. This is a cost decision, not permission to share tables.
- Messaging: SQS Standard in AWS; an SQS-compatible ElasticMQ instance locally. No memory-only message bus.
- Outbox, Inbox, idempotency, an orchestrated Saga, compensations, and optimistic concurrency.
- Basic real authentication using ASP.NET Core Identity in Gateway.Web. No public registration or external OIDC provider in the first delivery.
- The UI supports every published human operation. Internal commands run through these workflows, rather than through buttons that fabricate results.

## Reading and usage
1. Read `specs/01-product.md` and `specs/02-architecture.md`.
2. Consult contracts and rules in `specs/03-contracts.md` and `specs/04-workflows.md`.
3. Review the UI, security, and study-topic mapping in `specs/05-blazor-and-learning.md`.
4. Review costs and deployment in `specs/06-aws-and-costs.md`.
5. Implement tasks from `tasks/implementation-plan.md` and verify `specs/07-acceptance.md`.
6. Copy `AGENTS.md`, `specs/`, `tasks/`, and `prompts/` into the repository used by Codex. Start with the LAB-001 prompt.

All project documents, explanations, code names, contracts, tests, and implementation prompts must be in English. This English edition supersedes the earlier instruction to write learning explanations in Spanish. The existing study roadmap remains unchanged: this lab applies its topics, does not mark them as mastered, and does not replace its files.

## Time scope
Suggested sequence, not a guarantee: day 1, structure and persistence; day 2, complete workflow; day 3, failures and Blazor; day 4, AWS and rehearsal. If time is limited, deploy the tested basic workflow and complete advanced scenarios afterward. Never claim a pattern is complete without evidence.

## Costs
Local execution: no cloud fees. Neon: Free plan within its limits. AWS Fargate: billable consumption, not permanent free hosting. Run it only during practice sessions and stop it afterward. Estimates and cleanup procedures are in the AWS specification.

## Out of scope
No real payment cards, SMTP, maps, AI, chat, coupons, Kubernetes, Redis, managed RabbitMQ, multiple regions, domain purchases, or multitenancy. Do not add every roadmap pattern artificially. Do not introduce subscriptions or commercial libraries to complete the lab.

## LAB-006 operations

Admin controls at `/operations` select one service, show bounded redacted diagnostics, receive up to ten DLQ messages and replay an original delivery after correcting its failure. `/tour-projections` shows Reservations' informational price version and update time. Neither the projection nor a replay creates a new authoritative price, charge or message identity. All backend operations enforce authorization.

Run `./scripts/Verify-Operations.ps1` for real slow-Catalog breaker recovery, pause/reset, projection ordering, trace context and DLQ duplicate/lease tests. The full demo, bounded retry decisions, private configuration and visibility semantics are in [docs/operations-execution.md](docs/operations-execution.md); actual results are in [docs/evidence/LAB-006.md](docs/evidence/LAB-006.md).

Optional local traces:

```powershell
docker compose -f compose.yaml -f compose.tracing.yaml --profile tracing up -d
# Open http://localhost:16686 (Jaeger); create a reservation and find its traces.
# Restore the base session afterward:
docker compose -f compose.yaml -f compose.tracing.yaml --profile tracing stop otel-collector jaeger
docker compose up -d --wait gateway catalog reservations payments notifications
```

The base application remains independent of collector/viewer availability. Readiness is on demand and checks only the owning database; no background database metrics scraper was added. Stop application tasks between sessions to let Neon suspend in a later AWS deployment; a fault pause is not a cloud cost pause. No AWS resources were deployed.
