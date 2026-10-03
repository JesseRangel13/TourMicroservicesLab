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
