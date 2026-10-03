# TourMicroservicesLab — Complete Specification Package



---

**File: README.md**

# TourMicroservicesLab — Specifications for Codex

Version 1.1 — October 2, 2026. Jesse's student project. English edition.

## Objective
Build a small tour application using .NET 10, a Blazor frontend, four microservices, real PostgreSQL, and a temporary deployment on AWS ECS/Fargate. Payments and email are simulated, with durable state. The priority is to learn distributed-system decisions and demonstrate them in an interview, without turning this lab into a commercial platform.

This package contains specifications and instructions. It does NOT contain an implemented application or provisioned AWS resources.

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


---

**File: AGENTS.md**

# Project Instructions

## Source of truth
Read README.md and the specifications before editing. `specs/04-workflows.md` defines transitions and invariants; `specs/03-contracts.md` defines contracts. If a contradiction affects simulated money, inventory, security, or cost, document it and resolve it explicitly before implementing that behavior. Do not silently invent business rules.

## Implementation
- .NET 10 is required; enable nullable reference types. Code, tests, explanations, and learning documentation must all be in English.
- Execute small tasks in order. Inspect and reuse existing work; do not recreate the repository for every prompt.
- Prefer concrete classes for simple logic and interfaces for replaceable boundaries: payment provider, sender, clock, transport, and Catalog client.
- Use lightweight CQRS with explicit typed handlers. MediatR is not required; separating commands from queries does not require a library.
- Do not impose a generic repository over EF Core, event sourcing, a reflection-based dispatcher, or an interface for every class.
- Do not share EF entities, DbContext, or business rules across services. Shared.Contracts contains only integration contracts and transport primitives.
- No communication between services through C# events or access to another service's tables.
- Singleton workers create an async scope per work item or batch. Use a scoped DbContext in APIs and a per-operation context for Identity/Blazor through a factory where appropriate; never retain a context for the lifetime of a circuit.
- Propagate CancellationToken end to end. No .Result, .Wait, async void outside appropriate event handlers, Task.Run for I/O, or essential fire-and-forget work.
- Use HttpClientFactory and explicit limits. No indiscriminate retries for non-idempotent effects.
- Do not put JWTs, passwords, private keys, or connection strings in logs, Git, or versioned tfvars.
- Do not disable TLS validation; explicitly generate and trust the lab CA.

## Evidence
Each task delivers buildable code, relevant checks, execution documentation, and requirement-to-test evidence. Do not invent results. If an SDK, Docker, or AWS is unavailable, record the limitation and provide reproducible commands without claiming they ran.
Every simulation must be labeled. A fake payment means a fictional provider, NOT volatile state or missing idempotency.
Do not claim exactly-once message delivery. The transport permits duplicates and reordering.
No mandatory agent delegation. On completion, summarize changes, verification, limitations, and the next task.

## Scope and cloud
Create infrastructure and scripts as reviewable artifacts. Do not deploy resources or activate paid plans as part of a local implementation task. Deployment is a separate, explicit task. Do not use production resources or publish unrestricted endpoints. Do not delete data to clean up a test. Destruction scripts affect only resources carrying this lab's prefix and tags.


---

**File: specs/01-product.md**

# 01 — Product and Business Rules

## Users and capabilities
Seed two tourists, Alice and Bob, and one Admin. Initial passwords come from private configuration, never literals in the repository. Identity stores password hashes. No public registration or password-recovery workflow.

Tourist: sign in and out; list and filter tours; inspect dates, prices, and capacity; request a reservation; view their reservations and progress; cancel a confirmed reservation before departure; inspect their simulated notifications.
Admin: all views with filters; create/edit tours; create tour sessions; adjust capacity; change prices; activate/deactivate tours; inspect Sagas, payments, Outbox, Inbox, and DLQs; select failure scenarios; reconcile uncertain payments; retry compensations. No direct state editing to Confirmed or Succeeded.

## Minimum model
Catalog:
- Tour(Id UUID, Name, Description, Active, Version).
- TourSession(Id UUID, TourId, StartsAtUtc, UnitAmountMinor long, Currency='MXN', Capacity, AvailableSeats, PriceVersion, Version).
- Quote(Id, unique RequestId, SessionId, Participants, UserId, UnitAmountMinor, TotalAmountMinor, Currency, ExpiresAtUtc).
- SeatHold(Id=HoldId, SagaId, SessionId, Participants, QuoteId, Status, ExpiresAtUtc, Version).
Reservations:
- Reservation(Id, UserId, SessionId, Participants, QuoteId, immutable agreed price, Status, CreatedAtUtc, Version).
- ReservationSaga(Id, ReservationId, State, HoldId, PaymentOperationId, RefundOperationId, DeadlineUtc, Version, FailureReason, required/completed compensations).
- IdempotencyRequest(UserId, OperationName, Key, RequestHash, ResourceId, ResponseStatus, ResponseBody).
- TourProjection(SessionId, Name, informational price, PriceVersion, UpdatedAtUtc).
Payments:
- PaymentOperation(Id, SagaId, ReservationId, AmountMinor, Currency, Status, ProviderReference, NextReconcileAtUtc, Version).
- RefundOperation(Id, PaymentOperationId, AmountMinor, Status, Version).
- FakeProviderOperation(unique OperationId, Kind, RequestHash, Status, reference).
Notifications:
- Notification(Id, UserId, ReservationId, SourceEventId, Kind, Subject, Body, Status, CreatedAtUtc, SentAtUtc).
Gateway identity: its own users, roles, and Identity tables, without joins to business-service tables.
Each consumer has an Inbox; each publisher has an Outbox. Reservations maintains Saga transition history.

## Required rules
R-01 Money: integer minor units (cents), MXN only, positive amounts, checked multiplication. Do not use double for money.
R-02 Participants must be between 1 and 6; the session must be in the future; no overselling. Capacity cannot be reduced below occupied or held seats.
R-03 A Quote lasts 5 minutes. HoldSeats must arrive before it expires. A valid quote preserves the accepted price even if the current price changes. Deactivating a tour prevents new holds.
R-04 A Hold lasts 10 minutes from creation. Confirmation requires Held status and must occur before expiration. Server UTC time is authoritative; clients do not determine expiration.
R-05 Confirmed requires a successful charge and confirmed seats. Unknown never means Declined.
R-06 A user may make multiple reservations for the same session. Retries of one intention use the same idempotency key.
R-07 User cancellation is allowed only from Confirmed and before StartsAtUtc. Cancelling a nonterminal request returns 409; do not build that additional workflow in the MVP. Repeating an already initiated cancellation returns the same process without another refund.
R-08 Cancellation is complete only after both the refund and seat release are confirmed. Show CancellationPending while either is outstanding; use ManualReview when automatic recovery cannot complete.
R-09 A terminal reservation failure without a charge ends in Failed after releasing any hold. A charge that cannot lead to confirmation ends in Failed after refund and release. Do not publish success prematurely.
R-10 No actual email is sent: the simulated sender records delivery. The UI labels payments and email as simulated.
R-11 Payments use a stable identifier per operation. A refund uses a different stable identifier linked to the original charge.
R-12 Persisted states survive process restarts and environment shutdown. Preserve queues and the database when pausing.

## MVP and essential topics
Implement success and persistence first, then failure invariants. Blazor must cover every human operation in the contract. The local projection is informational; it never authorizes price or capacity by itself. The lab's value is the complete workflow and recovery, not the number of projects.


---

**File: specs/02-architecture.md**

# 02 — Architecture

## Processes
Five processes and five independent images:
1. Gateway.Web: Blazor Interactive Server, Identity, and YARP.
2. Catalog.Api: catalog, quotes, availability, and inventory consumers.
3. Reservations.Api: reservation API, persistent orchestrator, consumers, and timers.
4. Payments.Api: query/admin API, consumers, fake provider, and reconciliation.
5. Notifications.Api: query API, consumer, and fake sender.

Initially run one instance of each in AWS. Add replicas only for explicit tests. Co-hosting the UI and Gateway saves one task; do not merge the four business domains into one task or share their DbContext. Document that Identity is co-hosted for simplicity and an external OIDC provider would be an evolution.

## Suggested structure
src/Gateway.Web
src/Catalog.Api
src/Reservations.Api
src/Payments.Api
src/Notifications.Api
src/Shared.Contracts
src/Shared.Infrastructure
tests/Unit, Integration, Contracts, EndToEnd
infra/terraform
scripts (PowerShell and/or shell, with a README)
docs/learning-map.md, demo-guide.md, decisions/, evidence/

Each service may use Domain, Application, Infrastructure, and Endpoints folders within one project. Separate assemblies for every layer are unnecessary. Shared.Infrastructure may contain reusable plumbing (envelopes, OpenTelemetry, transport/basic Outbox support), without business entities or cross-service queries. Keep contracts small and versioned through the envelope; do not publish EF entities.

## Persistence
Use real PostgreSQL in both environments. Do not substitute EF InMemory or SQLite for transactional integration tests.
One physical database with five schemas: catalog, reservations, payments, notifications, identity. Identity/Data Protection are Gateway-owned data, not business data the UI can query. Separate runtime roles, restricted to their schema; use a separate privileged provisioner/migrator that is never supplied to business tasks. No cross-schema joins, domain-crossing foreign keys, or SELECT/UPDATE against another service's tables. Each schema/user has its own Outbox and Inbox tables.
Actually test PostgreSQL permissions; a search_path alone is insufficient. Revoke unnecessary public access and creation in public; check inherited provider roles. If Neon cannot support the selected permissions model, document and resolve the limitation before deployment without pretending isolation exists.
Use separate EF migrations and a migration-history table per schema. Run a job/CLI before services start; do not have five replicas migrate on startup. Automatic local bootstrap and idempotent seeding. No destructive reseeding in AWS.
Keep runtime pools small per service (initial maximum 5 connections; adjust through testing); close/dispose per operation. Use a direct connection for migrations and a compatible Neon pooling option for runtime. Verify TLS hostname and certificate.

## Durable transport
Four input queues: catalog, reservations, payments, notifications; one DLQ per queue. SQS Standard in AWS and ElasticMQ in Compose. The local adapter uses the AWS SDK with a configured endpoint and dummy credentials; cloud uses an IAM task role, not static access keys.
SQS is a queue, not a broadcast bus. A routing table defines consumers per message type. For an event with two recipients, the publisher creates two Outbox records, one per destination, within its transaction. Both have the same EventId and different DeliveryIds. Inbox deduplicates by (ConsumerName, EventId).
The publisher preserves identifiers on retries. Mark Outbox as sent only after successful SendMessage. Persistent leases/atomic claiming support multiple workers. Publish outside the database transaction, without retaining database locks during network calls.
Consumer: validate the envelope, execute the local transaction (Inbox + effects + Outbox), commit, then DeleteMessage. Do not ACK transient errors. A malformed message or incompatible version reaches the DLQ after maxReceiveCount=5. Never silently discard it. Initial visibility is 60 seconds, long polling 20 seconds, and batch size up to 10; extend visibility when required. Keep individual fake calls short rather than blocking a consumer for minutes.

## APIs and CQRS
Use Controllers or Minimal APIs consistently per service. Separate Commands/Queries with explicit handlers and validation. Neither MediatR nor a purchased license is required. Use EF directly in query handlers and concrete repositories when they encapsulate useful invariants. ASP.NET middleware handles exceptions, authentication, authorization, and correlation. Add validation/logging handler decorators only if they remove real repetition; explain how they differ from middleware.

## Resilience
Use HttpClientFactory for Gateway clients and the Catalog client. For idempotent quote creation: a 3-second per-attempt timeout, at most 2 retries with backoff/jitter, and a configurable 10-second total limit. The circuit breaker opens after 5 consecutive failures per instance, stays open for 30 seconds, and probes recovery; document that its counter is not global across replicas. Start with a limit of 10 simultaneous calls per instance; explicitly reject excess load.
Transport retries and handler retries must not multiply without limits. Distinguish user cancellation from an upstream timeout. Retry payment effects only with a fixed operation ID and a deduplicating provider. Graceful shutdown, scopes per batch, and cancellation; never discard durable work on shutdown.

## Observability
JSON logs include service, event, ReservationId, SagaId, MessageId, OperationId, TraceId, and SpanId. Propagate traceparent/tracestate and correlation through HTTP and envelopes. A consumer creates a receive span and associates the message context. A long Saga may span multiple traces.
Use OpenTelemetry for ASP.NET, HttpClient, and handler spans; offer an optional local collector/Jaeger profile. Basic AWS profile: stdout to CloudWatch, with application traces/metrics exported to logs. Do not claim a complete AWS tracing backend unless a collector/backend was deployed. The study dashboard reads business history through APIs rather than parsing logs as the source of truth.
Metrics: pending Outbox count/age, Sagas by state, ignored duplicates, payment outcomes, dependency failures, and DLQ message counts. Do NOT use reservation IDs as high-cardinality metric labels. Liveness must not query the database. Readiness is separate and must not continuously poll in a way that keeps Neon awake. Readiness does not require every downstream service to be available.


---

**File: specs/03-contracts.md**

# 03 — HTTP and Message Contracts

## Conventions
/v1, camelCase JSON, UUIDs, ISO-8601 UTC timestamps, and integer minor units for money. Pagination: page>=1, pageSize 1..50. ProblemDetails includes a stable code and traceId, without stack traces or secrets. Use 400 for invalid input, 401 for missing/invalid identity, 403 for insufficient permissions, 404 for missing or another user's resource, 409 for business conflicts, 503 for an unavailable required dependency, and 202 for accepted durable work.
All protected APIs validate JWTs; login and liveness endpoints are explicit exceptions. A role restriction in the UI alone is not authorization.

## Human-facing surface: every operation requires a Blazor screen or control
Gateway Identity: POST /auth/login (form + antiforgery), POST /auth/logout, POST /auth/token (verified credentials for API tools; rate limited), GET /auth/me. Cookies for Blazor; JWTs for APIs. No endpoint that issues a token merely by selecting a name or role.
Catalog, through /api/catalog:
- GET /v1/tours?search=&page=&pageSize= — list.
- GET /v1/tours/{tourId} — detail.
- GET /v1/tours/{tourId}/sessions — dates/prices/capacity.
- POST /v1/admin/tours — name, description.
- PUT /v1/admin/tours/{tourId} — name, description, active, expectedVersion.
- POST /v1/admin/tours/{tourId}/sessions — startsAtUtc, unitAmountMinor, capacity.
- PUT /v1/admin/sessions/{sessionId}/price — unitAmountMinor, expectedVersion; increment PriceVersion and publish TourSessionChanged.
- PUT /v1/admin/sessions/{sessionId}/capacity — capacity, expectedVersion; never below occupied/held seats.
Reservations, through /api/reservations:
- POST /v1/reservations, mandatory Idempotency-Key header; body: sessionId, participants, expectedUnitAmountMinor, expectedCurrency. Obtain UserId from the token.
- GET /v1/reservations — own reservations only; Admin may filter by userId/status.
- GET /v1/reservations/{id} — snapshot, agreed price, status, and reason.
- POST /v1/reservations/{id}/cancel, Idempotency-Key; start compensation, not a destructive DELETE.
- GET /v1/reservations/{id}/timeline — business transitions without secret messages.
- GET /v1/admin/sagas?state= — process state and deadlines.
- POST /v1/admin/sagas/{id}/retry-compensation — same operation, not a new refund ID.
- GET /v1/projections/tour-sessions — informational copy with updatedAt.
Payments, through /api/payments:
- GET /v1/payments?reservationId= — own records or Admin.
- GET /v1/payments/{id} — outcome and associated refund.
- POST /v1/admin/payments/{id}/reconcile — query the provider with the original ID; do not initiate another charge.
Notifications, through /api/notifications:
- GET /v1/notifications?reservationId=&page=&pageSize= — own records or Admin.
- GET /v1/notifications/{id} — fake content and status.
Administration on each business service:
- GET /v1/admin/diagnostics — counters, redacted Outbox/Inbox data, and worker status.
- PUT /v1/admin/faults — permitted mode and occurrence limit; only when LabFeaturesEnabled.
- POST /v1/admin/faults/reset — restore normal behavior.
- GET /v1/admin/dead-letters — redacted list from its DLQ, pageSize<=10.
- POST /v1/admin/dead-letters/{deliveryId}/replay — send to its original queue, preserving MessageId. Replay only after correcting the error and write an audit log. Do not expose receipt handles to the browser. SQS inspection has its own visibility/lease semantics. Replay confirms successful sending before removing the DLQ message. Document that listing a DLQ is not a passive SQL query.

## Internal quote contract
POST Catalog /v1/internal/quotes, accessible only to the Reservations service identity with quote:create permission. Include a stable requestId per (userId, reservation-create idempotencyKey), sessionId, participants, userId, expectedUnitAmountMinor, and expectedCurrency. Return quoteId, unitAmountMinor, totalAmountMinor, currency, expiresAtUtc, and session information.
Repeating requestId with identical input returns the same quote while it remains usable; different input returns 409. A price different from the accepted price returns 409 PriceChanged, without a charge. An expired quote returns 409; the client needs a new intention and must confirm the price. YARP must not publish this route; Blazor cannot invoke it directly.

## Durable response example
POST reservations returns 202, Location=/api/reservations/v1/reservations/{id}, and:
{ "reservationId":"UUID", "status":"AwaitingAvailability", "sagaId":"UUID" }
A retry with the same key preserves the resource and original response. If the original request is still processing, return a documented and tested retryable temporary conflict or a result lookup. Do not fabricate success before commit.
If Catalog fails during quoting, return 503 and create neither a Saga nor a payment; reuse the key on a later retry. Deferred PendingValidation is a documented extension, not hidden MVP behavior.

## Envelope
{
  "messageId":"UUID", "deliveryId":"UUID", "type":"ProcessPayment", "version":1,
  "occurredAtUtc":"2026-10-02T22:00:00Z", "sagaId":"UUID",
  "correlationId":"UUID", "causationId":"UUID", "traceparent":"...", "payload":{}
}
No JWTs, private keys, or card details in payloads. Include required identities and amounts from authorized sources. Validate type/version/fields; never silently replace missing required UUIDs or values.

## Routes and minimum payloads
| Type | Publisher → consumer | Fields in addition to SagaId |
|---|---|---|
| HoldSeats | Reservations → Catalog | holdId, quoteId, reservationId, userId |
| SeatsHeld | Catalog → Reservations | holdId, expiresAtUtc |
| SeatsHoldRejected | Catalog → Reservations | holdId, reason |
| ProcessPayment | Reservations → Payments | paymentOperationId, reservationId, userId, amountMinor, currency |
| PaymentSucceeded | Payments → Reservations | paymentOperationId, providerReference |
| PaymentDeclined | Payments → Reservations | paymentOperationId, reason |
| PaymentOutcomeUnknown | Payments → Reservations | paymentOperationId, nextReconcileAtUtc |
| ReconcilePayment | Reservations → Payments | paymentOperationId |
| ConfirmSeats | Reservations → Catalog | holdId |
| SeatsConfirmed | Catalog → Reservations | holdId |
| SeatsConfirmationRejected | Catalog → Reservations | holdId, reason |
| SeatsHoldExpired | Catalog → Reservations | holdId |
| ReleaseSeats | Reservations → Catalog | holdId, reason |
| SeatsReleased | Catalog → Reservations | holdId |
| RefundPayment | Reservations → Payments | refundOperationId, paymentOperationId, reservationId, userId, amountMinor, currency |
| PaymentRefunded | Payments → Reservations | refundOperationId, paymentOperationId |
| RefundNeedsReview | Payments → Reservations | refundOperationId, reason |
| ReservationConfirmed | Reservations → Notifications | reservationId, userId, summary |
| ReservationCancelled | Reservations → Notifications | reservationId, userId, summary |
| ReservationFailed | Reservations → Notifications | reservationId, userId, reason |
| TourSessionChanged | Catalog → Reservations | sessionId, tourName, active, priceVersion, unitAmountMinor, currency |

Unknown is not a terminal event; a later outcome may resolve it. Projection consumers ignore older versions even when they arrive later. Commands carry a fixed OperationId/HoldId; new deliveries do not create new IDs. Shared.Contracts supports serialization tests independent of EF.


---

**File: specs/04-workflows.md**

# 04 — Workflows, Transactions, and Recovery

## W-01 Creation and success
1. Authenticate/authorize and validate the request. Compute a canonical content hash. Look up idempotency results by user + operation + key.
2. Request an idempotent quote from Catalog; verify the accepted price and validity. Do not hold a database transaction open during HTTP.
3. Reservations transaction: insert the unique key, reservation, AwaitingAvailability Saga, and HoldSeats intent into Outbox. Resolve concurrent requests through the unique constraint and read the winning result after rollback.
4. Catalog consumes HoldSeats: record Inbox and validate the quote; decrement AvailableSeats through a conditional update requiring AvailableSeats>=Participants, insert the Hold, and add SeatsHeld to Outbox within the same transaction. An inventory conflict produces SeatsHoldRejected. Never use an unprotected SELECT followed by UPDATE.
5. Reservations consumes SeatsHeld: transition to AwaitingPayment and add ProcessPayment to Outbox; use the already persisted ID. The payment-resolution deadline is 2 minutes from the request, without exceeding the hold's validity.
6. Payments persists a Pending request and ACKs after commit. A separate worker queries/calls the fake provider outside any long database transaction. The provider persists its effect with a unique key in its own transaction; then Payments saves the result and Outbox. If the process crashes between those steps, reconcile by operation ID.
7. PaymentSucceeded → AwaitingConfirmation + ConfirmSeats.
8. Catalog atomically confirms an unexpired Held record and adds SeatsConfirmed to Outbox. Do not change AvailableSeats on confirmation: holding already deducted them.
9. SeatsConfirmed → Confirmed + ReservationConfirmed for Notifications. Inbox + state + history + Outbox always belong to each consumer's local transaction.
10. Notifications creates a Pending record and ACKs. The fake sender worker durably records delivery idempotently by business event and marks Sent. Its UI shows Simulated and never claims actual SMTP delivery.

## W-02 Capacity, quote, or payment failure
Invalid/expired quote: 409 before creating the Saga. Catalog unavailable: 503 before the Saga.
SeatsHoldRejected: Failed without payment. PaymentDeclined: initiate ReleaseSeats and remain Compensating; after SeatsReleased, transition to Failed. Preserve the reason.
Catalog expires Held records through a worker, with an atomic transition and exactly one capacity restoration; publish SeatsHoldExpired. A later release recognizes that release already occurred and emits the required confirmation without restoring capacity again.

## W-03 Timeout after a charge
Fake mode TimeoutAfterCharge: the provider durably records Succeeded and then returns/throws a timeout once. PaymentOperation becomes Unknown, not Declined. The worker queries the provider using the same ID; finding Succeeded publishes PaymentSucceeded once per business transition.
If there is no verifiable result by the deadline, mark the Saga PaymentUncertain and request ReleaseSeats; prevent late confirmation. Reconcile with at most 5 queries and configurable intervals (for example 5, 10, 20, 30, 30 seconds), then ManualReview. The hold has an independent expiration. An unresolved Unknown never becomes Failed while asserting that no charge occurred.
If success arrives after the decision to release/expire the hold, request a refund using the predefined RefundOperationId. Do not confirm an abandoned reservation. If the definitive result is Declined/NotCharged, finish Failed after releasing capacity. Do not resolve ambiguity with a new ProcessPayment operation.

## W-04 Compensation after confirmation failure
SeatsConfirmationRejected after a charge: enter Compensating and persist RefundPayment and ReleaseSeats in Outbox. Maintain two durable flags: RefundCompleted and SeatsReleased. Response order does not matter. Finish Failed and notify only after both flags are true. Payments and Catalog do not share a transaction.
Compensation may fail. Expose ManualReview, the reason, and pending operations. Admin retry reuses identifiers and continues from the flags; it never repeats the charge or creates another full refund.

## W-05 Cancellation
From Confirmed and before the session starts: a transaction fixes one RefundOperationId, sets CancellationPending, and adds RefundPayment and ReleaseSeats. Set Cancelled only after both results. Endpoint and workflow idempotency protect double clicks and different cancellation keys for the same resource. The MVP refunds the full amount without fees. Release may release a Confirmed hold in this workflow; restore capacity once. After departure time, return 409.

## W-06 Waiting for availability and late results
AwaitingAvailability has a configurable 30-second deadline. If no result arrives, start abandonment using ReleaseSeats with the original HoldId; finish Failed after release confirmation. A late SeatsHeld during abandonment does not start payment: keep the release in progress. Catalog handles ReleaseSeats before HoldSeats through a persistent HoldId tombstone, so a late hold command cannot recreate the hold. This also protects against out-of-order replay. Retain tombstones for the same deduplication period as Inbox. Do not finish on timeout while allowing a future hold to remain uncleared.
AwaitingConfirmation has a 30-second deadline. When it expires, initiate charge compensation and seat release; a late confirmation does not return the Saga to Confirmed. If SeatsConfirmed arrived first and won the transition, the timer reloads state and does not compensate. Use Version and transitions, not cross-replica in-memory locks.

## State and out-of-order messages
Minimum states: AwaitingAvailability, AwaitingPayment, PaymentUncertain, AwaitingConfirmation, Confirmed, Compensating, CancellationPending, Cancelled, Failed, ManualReview.
Every transition checks expected state, related identifiers, and Version. History persists from/to, reason, timestamp, and messageId. Do not advance based only on an event name.
An old, compatible event whose effect is already satisfied becomes an audited no-op + ACK. Impossible events or contradictory data generate an alert/DLQ entry rather than overwriting terminal state. A late payment success after abandonment requires compensation, even if the process already ended Failed with no known charge. It may reopen repair as Compensating; do not change it to Confirmed. Preserve history and do not publish another duplicate ReservationFailed at repair completion.
Expiration/confirmation race: Catalog serializes changes on Hold + Session. Confirmation requires Held and expiresAt>now. Only one transition wins. Duplicate expiration/release/confirmation must not alter inventory twice. Use a consistent locking order to avoid deadlocks.

## Outbox and Inbox
Outbox: claim batches with a lease/SQL SKIP LOCKED or a tested equivalent. Lease expiration enables recovery. Send outside the transaction and record publication with an ownership check. Two destinations mean two deliveries, not two business events.
Inbox: unique(ConsumerName, MessageId); insert + effects + Outbox in one transaction. Detect PostgreSQL duplicate conflicts after rollback; do not keep using an aborted transaction. ACK only after commit or a safe no-op.
Business idempotency: unique PaymentOperationId/RefundOperationId/HoldId, plus state checks. New messages concerning the same operation do not create another effect.
Retain deduplication data at least as long as queue + DLQ retention; the lab retains it for 14 days. Do not purge before 14 days or during normal pause. Replaying older messages requires reviewing the retention boundary; do not promise eternal protection.

## Per-service lab failure modes
Catalog: None, QuoteTimeout (bounded delay), RejectNextConfirmation, PauseConsumption. Reservations: None, PauseOutbox. Payments: the provider modes below and PauseConsumption. Notifications: None, FailureThenSuccess. Persist configuration per service, with limited occurrences and auditing. Pausing never purges queues or removes Inbox. Admin may reset modes. Crash points are test barriers/scripts, not UI-accessible crash endpoints.

## Fake provider and sender
IPaymentProvider: ChargeAsync, GetChargeStatusAsync, RefundAsync, GetRefundStatusAsync. All accept CancellationToken and a stable ID. Modes: Success, Decline, TimeoutAfterCharge, UnknownUntilAdminResolution, RefundTransientFailure (2 failures, then success). Persist the mode per operation when creating it; changing configuration does not magically alter an existing charge.
Admin resolution of UnknownUntilAdminResolution modifies only the authorized fictional provider result, with auditing and a clearly simulated UI. Then perform actual workflow reconciliation rather than editing Saga state. Add POST /v1/admin/fake-provider/{operationId}/resolve with Succeeded or NotCharged and the corresponding UI control.
INotificationSender fake stores a durable unique receipt by SourceEventId + Kind. The notification operation also checks ReservationId + Kind to avoid creating the same terminal notice again through a different event. FailureThenSuccess tests 2 transient failures. Inbox must not suppress Pending work that failed after message consumption.


---

**File: specs/05-blazor-and-learning.md**

# 05 — Blazor, Security, and Roadmap Application

## Required frontend
Blazor Web App .NET 10 Interactive Server, co-hosted in Gateway.Web. No specialized visual design: basic layout, forms, tables, labels, and messages. No commercial component frameworks. Clear navigation between tours, reservations, payments, notifications, and administration.
Screens:
- Login/logout (SSR forms to establish cookies; antiforgery).
- Tours: paginated search, detail, sessions, quantity, price, and reserve button.
- Reservations: own list and detail with timeline, status, agreed price, and cancellation.
- Payments: own list/detail with charge/refund/Unknown and a simulated label.
- Notifications: list/detail of fake deliveries.
- Admin Catalog: create/edit/activate tours, create sessions, change price/capacity.
- Admin Processes: Sagas, filters, retry-compensation, uncertain payments and reconciliation, fake-provider resolution.
- Admin Diagnostics: service selector, redacted Outbox/Inbox data, DLQ and replay, permitted failures and reset, local tour projection with version and timestamp.

The UI covers every human endpoint in specification 03 and fake-provider resolution in 04. No button to publish an arbitrary PaymentSucceeded. Demonstrate message reordering/duplication through scripts or a limited, audited admin fault mode, not unrestricted browser access to the broker.
Blazor does not read business databases or SQS directly. Typed clients call the Gateway's /api routes, even when the server uses loopback. Use a per-user JWT per request; do not share DefaultRequestHeaders.Authorization across users. No secrets in the UI or tokens in localStorage. Read-only polling every 3 seconds only while the screen is active and the process is nonterminal, with cancellation/disposal; no infinite global polling loop.
Generate an idempotency key per intention and preserve it when retrying a timeout; generate a new key only for a new intention. Disabling double clicks helps UX, but does not guarantee uniqueness. Display temporary states, business errors, loading, and retry actions; do not rely on form validation alone.

## Identity and authorization
Gateway.Web uses Identity with the identity schema, password hashes, and HttpOnly + Secure cookies with appropriate SameSite settings. Three seeded users without public passwords; Tourist/Admin permissions. No open registration endpoint. Persist encrypted, access-restricted Data Protection keys in Identity-owned durable storage so cookies survive restarts. Configure key encryption with a certificate supplied as a secret; merely storing keys as plaintext is insufficient. The MVP has one Gateway replica; document circuit affinity or transport requirements before scaling Interactive Server circuits.
Lab JWT issuer in Gateway (do not claim OAuth/OIDC implementation): RS256 signature, explicit issuer tourlab-identity, audience tourlab-api, stable sub, role, and a 15-minute expiration. Only Gateway and the provisioner receive the private key. Each API receives the public key, validates signature/issuer/audience/lifetime, and enforces ownership. A shared audience represents one logical API resource in this lab, not blanket permission for every operation.
Blazor obtains identity through AuthenticationStateProvider, rather than retaining HttpContext for the circuit lifetime. Use Identity revalidation, secure login/logout, and expired-session handling. Client services are scoped per circuit without retaining DbContext; use short Identity operations and a factory where supported. Do not share DbContext across circuits or concurrent calls. JWTs issued for Blazor retain the verified sub/roles; a component cannot supply an arbitrary userId.
Reservations needs quotes from Catalog: use a separate service identity with issuer tourlab-services, audience catalog-internal, sub reservations, quote:create permission, and a 2-minute expiration, signed with a dedicated Reservations key. Catalog accepts that issuer only on the corresponding internal route. Never persist a user token in a Saga or message. Other workflows use SQS IAM publishing/consuming controls, not JWTs inside envelopes.
Ownership: take UserId from the token during creation, filter list queries, return 404 for another user's detail, and enforce Admin roles on the backend. Payments/Notifications store the owner received through infrastructure-authenticated messages; they do not query Reservations tables.
Use TLS externally and for external connections, plus internal API TLS with the lab CA. Do not configure unnecessary permissive CORS: the browser is on the same origin. Anti-CSRF protections for cookies/forms and human actions. Rate-limit login/token and admin fault endpoints. No stacks or secrets in ProblemDetails.

## Applying the actual roadmap: topics 1–17
Consulted source: docs/roadmap.md in the current topic 10 package, updated October 1. Do not invent topic numbers.
| Topic | Natural application | Expected evidence |
|---|---|---|
| 1 Value/reference/parameter passing | DTOs vs mutable entities; avoid sharing instances between work items | explain an accidental mutation |
| 2 Class/struct/record | records for contracts, classes for entities; a small readonly Money record struct if invariants justify it | decision and example |
| 3 Equality | ID/value-object equality; no reference equality for deduplication | key/value test |
| 4 Nullable reference types | Nullable enable; missing results and validated DTOs | build and null handling without arbitrary ! |
| 5 Generics/constraints/variance | Envelope<T>, Result<T>, or typed handlers with useful constraints | real use; variance only if an interface needs it |
| 6 Delegates/lambdas/closures/events | LINQ, resilience/configuration callbacks, safe captures | distinguish local C# events, domain events, and integration events |
| 7 LINQ/IEnumerable/IQueryable | projected/paginated EF queries, AsNoTracking, execution through ToListAsync | SQL and no unnecessary early materialization |
| 8 Exceptions | centralized unexpected exceptions; typed expected conflicts | ProblemDetails and logs without leaking secrets |
| 9 Async/await | async HTTP/EF/SQS, CancellationToken, bounded concurrency | cancellation without blocking |
| 10 DI/IoC/lifetimes/factories/Options | worker scopes, fake provider, clients, validated Options | detect and correct singleton-to-scoped misuse |
| 11 ASP.NET pipeline/APIs | middleware, auth, routing, binding, status codes | follow a request end to end |
| 12 EF/SQL | migrations, projections, indexes, transactions, versions | inventory race and generated SQL |
| 13 Testing/refactoring | unit/integration/contract/E2E tests of invariants | tests detect duplicates, unauthorized access, and overselling |
| 14 Concurrency/resources | Version, SemaphoreSlim limits if useful, await using | cross-replica conflict and disposal |
| 15 Architecture/SOLID/CQRS | handlers and provider boundaries, Saga, monolith-versus-microservices ADR | justify decisions without layers added by habit |
| 16 Security/distributed reliability | resource authorization, resilience, Outbox/Inbox/idempotency | timeout after a durably simulated effect |
| 17 Docker/ECS/observability | multistage images, roles, health/shutdown, correlation | documented local and cloud demos |

Do not introduce ref/out, covariance, hand-built expression trees, locks, or .NET events unless they solve a project problem. Mark them "not needed" and explain why. EF already uses expression trees through IQueryable; do not build a dynamic engine just to demonstrate them.
Explain OAuth/OIDC/PKCE, MediatR, SOAP, Jenkins/Sonar, and Strangler as evolution/comparison topics when not implemented. Do not claim Identity + lab JWT implements OIDC. Add docs/learning-map.md with concrete code paths, concepts, exercises, and interview questions, all in English. Do not list examples that do not exist.


---

**File: specs/06-aws-and-costs.md**

# 06 — AWS Fargate, Free PostgreSQL, and Costs

## Lab profile
Initial region: us-east-1. Five ECS Services, Linux x86_64, one task per service. Start at 0.25 vCPU and 0.5 GB per task ONLY if actual startup/load/memory tests support it. The Blazor Gateway may need 1 GB; increase explicitly and recalculate. Initially use on-demand Fargate so Spot interruptions do not complicate the first diagnosis.
Use a dedicated VPC with a public subnet and Internet Gateway. assignPublicIp=true permits ECR, SQS, and Neon access without NAT. A public IP does not mean an open port. Security groups: Gateway 8443 only from the student's ClientCidr; Catalog 8443 only from Gateway and Reservations; Reservations/Payments/Notifications 8443 only from Gateway. No public ingress to business services. Permit necessary egress for AWS/Neon HTTPS and internal connections. Separate SGs; do not allow inbound 0.0.0.0/0 for convenience.
Cloud Map private DNS tourlab.internal, private A records, short TTL, one name per service, fixed port 8443. Each HttpClient recycles connections to accommodate DNS changes. Private DNS is NOT a complete HTTP load balancer. No Service Connect sidecar or ALB in the initial low-cost profile.
Each service/image deploys independently; the UI is co-hosted in Gateway. Terraform manages Cloud Map, SQS, and roles. No mandatory additional remote Terraform backend; keep local state private and backed up, never in Git.

## TLS entry without buying a domain or ALB
Generate a private lab CA locally, never commit it. Gateway certificate SANs: lab.tours.test, gateway.tourlab.internal, localhost; internal certificates include each Cloud Map name. Explicitly distribute/trust the public CA certificate in clients and containers. Inject leaf private keys through secrets; keep the CA private key local only.
A script obtains the Gateway task's public IP and explains how to map lab.tours.test in the PC hosts file. URL: https://lab.tours.test:8443. The student trusts the local CA to open Blazor. Curl uses --cacert and --resolve. IP may change after restart: update mapping, do not hardcode it or disable SSL checks.
Do not claim a publicly browser-trusted certificate or a stable public endpoint. This profile serves the authorized PC. A future public demo may use ALB + ACM and an owned domain, with a separate estimate.

## Real, free PostgreSQL
Use Neon Free outside AWS, in a nearby matching region where available. One project, one database, five isolated schemas/roles. Do not provision RDS/Aurora by default. The user handles account setup and connection strings; bootstrap scripts do not print secrets. Verify plan/quotas in the console before use; never automatically enable a paid plan.
Official announcement dated October 2, 2026: Free includes 1 GB per project and 100 CU-hours/month per project. Quotas may change; older documentation still lists 0.5 GB. Keep the dataset below 100 MB, without images or bulk loads; verify transfer and connection limits in the current account. Database capacity does not mean unlimited free compute.
Outbox polling and timers can keep the database active, preventing scale-to-zero. This is acceptable during a session, but set all services to desiredCount=0 afterward to stop queries and allow suspension. Do not query the database for liveness every 10 seconds. Configure active polling and idle backoff; do not promise Neon suspension while queries continue.
Neon is outside the VPC: encrypted Internet traffic, possible billable transfer, and cold starts. Do not use real customer data. Document this teaching limitation rather than calling it a free production architecture.

## Reference estimate, not a quote or hard cap
Checked October 2, 2026; Linux x86 in us-east-1; taxes and credits excluded.
Public Fargate reference: CPU 0.0404784 USD/vCPU-hour; memory 0.004446 USD/GB-hour.
One 0.25 vCPU + 0.5 GB task: 0.0123426 USD/hour for compute.
Public IPv4: 0.005 USD/IP-hour.
Five tasks: (5×0.0123426)+(5×0.005)=0.086713 USD/hour for compute + IPv4.
- 8 total hours: approximately 0.69 USD.
- 40 total hours: approximately 3.47 USD.
- 730 hours: approximately 63.30 USD; do not leave it running all month.
If Gateway uses 1 GB, add 0.002223 USD/hour to those totals. Rolling deployments may temporarily double tasks, which also incur charges. Recalculate using actual sizes before apply.
Additional costs: Cloud Map and its private Route 53 hosted zone, ECR, CloudWatch, transfer to Neon, and possible KMS/SSM charges. A private hosted zone has a monthly fee (initial reference 0.50 USD/month; confirm current pricing), plus discovery resources/queries. SQS advertises 1M free requests/month shared across the account/regions under its terms; count Send/Receive/Delete/visibility actions, not only messages.
Indicative target: 1–5 USD for a short lab with a few dozen running hours and small data/log volumes. This is neither a guarantee nor authorization for recurring monthly spending. AWS credits cover charges only if the account is eligible and has a balance; do not assume credits.

## Required cost controls and operations
- Editable costs.md with region, actual sizes, hours, and estimated extras; preflight prints account/region/resource tags/estimate.
- Default Terraform desired_count=0. Creating infrastructure does not automatically start five services. Explicit startup script.
- stop-lab: sets all services to 0 and verifies zero tasks; preserves the database/queues. ECS does not guarantee automatic shutdown. A local script scheduled for a 2-hour session may fail if the PC shuts down; do not treat it as a hard cap.
- start-lab: validates configuration and owned resources, waits for health, prints IP/mapping, and reminds the user to stop afterward.
- Budget target 5 USD, configurable alerts. A Budget is not a spending limit and does not stop resources by itself. The budget email requires user input; do not invent it.
- destroy-lab: plan/review; target only resources tagged Project=TourMicroservicesLab and Environment=study. Do not delete Neon or schemas by default. Queue/data purging requires a separate explicit option from pause. Understand that destroy removes pending messages.
- CloudWatch retention 3 days; redact payloads. ECR retains 2 recent tags; clean unused images. Delete hosted zone/Cloud Map when finished to remove fixed costs. Stopping tasks does not eliminate all charges.
- No NAT Gateway, ALB, RDS, EFS, EKS, Redis, Secrets Manager, billable private endpoints, or custom KMS key in the base profile.
- Use standard SSM Parameter Store SecureString with a managed key where compatible; verify KMS charges and size limits. Terraform does NOT receive private keys/passwords as variables that would end up in state. Use separate compatible-sized PEM certificate/key parameters (<4 KB for standard parameters); validate limits instead of placing a large PFX in a standard parameter. A script creates sensitive parameters through private input; IaC references existing ARNs. Do not put secrets in logs or shell history.
- Task execution role: ECR pulls, logs, and only required parameters. Task role: Send to authorized destinations and Receive/Delete/ChangeVisibility on its own queue; replay only from its own DLQ. No sqs:* or AdministratorAccess. Gateway never receives business-database credentials.

## Delivery
Versioned Terraform, multistage Dockerfiles, reproducible/pinned builds, .dockerignore, non-root execution where appropriate, ports/health checks, and a one-shot migration script without exposing migrator credentials to runtime. Use the student's AWS profile/SSO, never root access keys. Local build-test script/CI; an optional GitHub/Jenkins pipeline must not block the demo.
AWS is not done until there is real evidence of healthy tasks, the UI, reservation/cancellation, persistence across pause/start, visible costs, and cleanup. Missing credentials/permissions block deployment, not infrastructure generation.

## Official sources
- https://aws.amazon.com/fargate/pricing/
- https://aws.amazon.com/vpc/pricing/
- https://aws.amazon.com/sqs/pricing/
- https://aws.amazon.com/cloud-map/pricing/
- https://aws.amazon.com/route53/pricing/
- https://aws.amazon.com/free/
- https://docs.aws.amazon.com/cost-management/latest/userguide/budgets-managing-costs.html
- https://neon.com/blog/neon-free-plan-1-gb-per-project
- https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-discovery.html


---

**File: specs/07-acceptance.md**

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


---

**File: tasks/implementation-plan.md**

# Task Plan

Work on one task at a time. Maintain this file with actual status and evidence. Do not automatically mark learning topics as mastered. Build incrementally: introduce a minimum UI early so the system is visible before completing fault injection.

| ID | Work | Depends on | Exit criterion |
|---|---|---|---|
| LAB-001 | .NET10 scaffold, PostgreSQL/ElasticMQ Compose, schema/role bootstrap, local HTTPS, Identity users, login/layout UI | — | Build, login, tested DB permissions, base images |
| LAB-002 | Catalog CRUD/sessions, quotes, atomic inventory, contracts | 001 | API and AC-04/17; basic catalog/admin UI |
| LAB-003 | Idempotent Reservations creation and state/timeline; Outbox/Inbox/SQS adapter without shared domain | 002 | AC-03/07/08/09 and quote 503; reservations UI |
| LAB-004 | Durable fake Payments, operations/reconciliation; successful hold→pay→confirm Saga; fake Notifications | 003 | AC-02/05/10/24; payments/notifications UI |
| LAB-005 | Saga compensations, expiration, cancellation, late results, concurrency | 004 | AC-06/11/12/13/14/15/16/22 |
| LAB-006 | Resilience, observability, projection, DLQ, admin/fault controls | 005 | AC-18/19/23; complete admin UI |
| LAB-007 | Auth/ownership hardening, Blazor scopes, UI E2E, learning map | 006 | AC-20/21/25/26/27/28/29; documentation |
| LAB-008 | Fargate IaC, external secrets, one-shot migrations, preflight/start/stop/destroy | At least 004; ideally 007 | terraform validate/reviewable plan and cost estimate; NO automatic deployment |
| LAB-009 | Explicit AWS deployment/smoke tests, pause/start, interview rehearsal | 008 | Actual AC-30 evidence and demo |

LAB-004 delivers a functioning vertical slice. LAB-005..007 complete resilience. Do not confuse a deployable prototype with verification of every pattern. IaC may be generated alongside local work, but agent delegation is neither required nor instructed.

## Dependency versions
Pin an available .NET10 SDK in global.json and compatible packages/images after verifying official sources. Avoid floating latest versions. Use compatible EF10/Npgsql10, YARP, AWS SDK, OpenTelemetry, and xUnit versions. MediatR is optional; do not require a paid license/package. Prefer a simple explicit dispatcher. No TODO placeholders in a workflow declared implemented.

## Each Codex delivery
State the problem resolved, observable behavior, relevant files, executed tests/checks and actual results, limitations, and next task. Add an ADR when changing a decision. Update docs/learning-map with existing examples. Never say "works in AWS" merely because Dockerfiles or Terraform exist.


---

**File: prompts/START-CODEX.md**

# Initial Prompt — Copy into Codex

I want to implement TourMicroservicesLab as a student project to practice microservices and prepare for Senior .NET interviews. The backend must use .NET 10. The frontend must be a simple Blazor Web App with Interactive Server, no visual-design work, and support for every human operation exposed by the services. Use real PostgreSQL locally and Neon Free for AWS. Payments and email are fake but durable. Target AWS ECS/Fargate with very low costs and session-based operation.

This package's files are already in the repository. Read AGENTS.md, README.md, every specification in specs/, and tasks/implementation-plan.md. First inspect the repository and available tools. Treat the specifications as the source of truth. Do not build a different project or add costly infrastructure.

Start with LAB-001 ONLY. Prepare a brief plan, then implement: a .NET10 solution, five host projects and minimum shared projects, Compose with PostgreSQL and ElasticMQ, example private configuration without real secrets, isolated schema/role bootstrap, Identity migration and configurable seeding, local TLS, Blazor login/logout and navigation shell, baseline proxy configuration, relevant tests, and an executable README. Do not generate the entire architecture in one pass or pretend future features are implemented.

Apply roadmap topics when needed. In this task prioritize nullability, appropriate classes/records, async/cancellation, DI/Options/lifetimes, ASP.NET middleware, and security. Do not introduce patterns artificially. Create docs/learning-map.md with actual code paths and docs/evidence/LAB-001.md with actual results. Write all code, documentation, explanations, and prompts in English.

Run builds and relevant checks if the environment permits. If SDK/Docker are missing, state precisely what you could not verify and provide commands for my PC. Finish with changes, verification, limitations, and next task LAB-002. Do not deploy AWS, create paid services, or touch production accounts.


---

**File: prompts/NEXT-TASK.md**

# Prompt for Each Subsequent Task

Continue TourMicroservicesLab from its current state. Read AGENTS.md, relevant specifications, tasks/implementation-plan.md, and the previous task's evidence. Implement only LAB-XXX (replace with the required task), respecting dependencies.

Reuse existing work. Before changing a contract, check its consumers and tests. Do not omit persistence, Inbox/Outbox, stable identifiers, or ownership rules from a workflow that requires them. Keep Blazor capable of invoking the human operations introduced by this task.

Add tests focused on observable invariants, execute relevant checks, and update docs/learning-map.md and docs/evidence/LAB-XXX.md. Explain limitations and new decisions. Do not mark unexecuted checks as executed. Keep all documents, code, and explanations in English. Do not deploy cloud resources unless this is explicitly LAB-009 and the user has defined the account, region, and budget.


---

**File: prompts/REVIEW.md**

# Independent Review Prompt

Review the TourMicroservicesLab implementation against specs/ and task evidence. Do not modify code during this first review. Focus on concurrent idempotency, inventory, commit before ACK, provider effects before responses, durable Saga state, expiration/late success, Inbox and Outbox atomicity, stable identifiers, schema isolation, resource authorization, worker/Blazor scopes, cross-user token leakage, TLS/secrets, Terraform costs, and cleanup.

Run available tests and search for cases that contradict invariants, not only existing test coverage. Report prioritized findings with the file, behavior, reproducible scenario, and suggested fix. Distinguish confirmed failures from unverified risks. Do not assume exactly-once delivery or free Fargate hosting. Do not claim verified deployment without access and actual evidence. Deliver a matrix of implemented/verified/pending requirements and a concrete next-task recommendation. Write the entire review in English.
