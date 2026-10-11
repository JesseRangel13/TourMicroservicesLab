# Tour Microservices — Technical Interview Study Guide & Architecture Defense

This study guide tracks your progress, key architectural concepts, concrete code references, diagnostic misconceptions, and ready-to-speak interview answers for the Tour Microservices Lab.

---

## 1. System Architecture Overview

```mermaid
flowchart TD
    User["Tourist / Admin Browser"] -->|HTTPS / Strict Cookie| Gateway["Gateway.Web (:8443)<br/>• SSR & Blazor Interactive Server<br/>• Identity & Data Protection<br/>• Per-Request Short-Lived JWTs<br/>• YARP Reverse Proxy"]
    
    Gateway -->|Internal Direct HTTPS / Bearer JWT| Catalog["Catalog.Api (:8444)<br/>• Schema: catalog<br/>• Tours, Sessions, Atomic Inventory<br/>• Authoritative Quotes & Seat Holds"]
    Gateway -->|Internal Direct HTTPS / Bearer JWT| Reservations["Reservations.Api (:8445)<br/>• Schema: reservations<br/>• Saga Orchestrator & State Machine<br/>• Informational Tour Projections"]
    Gateway -->|Internal Direct HTTPS / Bearer JWT| Payments["Payments.Api (:8446)<br/>• Schema: payments<br/>• Simulated Provider & Reconciliation<br/>• Charges & Refunds Idempotency"]
    Gateway -->|Internal Direct HTTPS / Bearer JWT| Notifications["Notifications.Api (:8447)<br/>• Schema: notifications<br/>• Simulated Email & Delivery Status"]

    Reservations -->|Synchronous HTTPS + Resilient Retry/Circuit Breaker<br/>Dedicated Service JWT (quote:create)| Catalog

    Catalog <-->|SQS Queues + DLQ<br/>Transactional Outbox / Inbox| Reservations
    Reservations <-->|SQS Queues + DLQ<br/>Transactional Outbox / Inbox| Payments
    Reservations -->|SQS Queues + DLQ<br/>Transactional Outbox / Inbox| Notifications

    subgraph Data Tier ["PostgreSQL 17 (Single Instance, Isolated Schemas & Roles)"]
        catalog_schema[("catalog schema<br/>role: catalog_user")]
        reservations_schema[("reservations schema<br/>role: reservations_user")]
        payments_schema[("payments schema<br/>role: payments_user")]
        notifications_schema[("notifications schema<br/>role: notifications_user")]
        identity_schema[("identity schema<br/>role: gateway_user")]
    end
```

---

## 2. Concept to Code & Test Reference Map

| Architectural Concept | Primary Repository Files | Key Verification Tests |
|---|---|---|
| **Service Boundaries & Contracts** | `src/Shared.Contracts/` (`CatalogContracts.cs`, `ReservationContracts.cs`, `PaymentContracts.cs`, `NotificationContracts.cs`) | `tests/Lab.Tests/CatalogContractTests.cs`, `CatalogHttpTests.cs` |
| **Atomic Inventory & Concurrency** | `src/Catalog.Api/Application/InventoryHandlers.cs`, `src/Catalog.Api/Persistence/CatalogDb.cs` | `CatalogTests.cs`, `IntegrationTests.cs` (Seat hold concurrency & overselling prevention) |
| **Authoritative Quotes vs. Projections** | `src/Catalog.Api/Application/QuoteHandler.cs`, `src/Reservations.Api/Application/CatalogQuoteClient.cs`, `src/Reservations.Api/Persistence/ReservationsDb.cs` (`TourProjections`) | `QuoteResilienceTests.cs`, `OperationsTests.cs` (`ProjectionIsAbsentBeforeInitialEventThenNewestWins`) |
| **Transactional Outbox & Inbox** | `src/Shared.Infrastructure/Messaging/` (`OutboxDispatch.cs`, `Workers.cs`, `MessageCodec.cs`, `SqsTransport.cs`) | `MessagingTests.cs` (`ConsumerCommitBeforeAckCrashRedeliversWithoutRepeatingInventoryOrSagaTransition`) |
| **Saga Orchestrator & State Machine** | `src/Reservations.Api/Application/` (`ReservationHandlers.cs`, `SagaCommands.cs`, `SagaCompensation.cs`) | `SagaWorkflowTests.cs` (15 end-to-end tests covering deadlines, late charges, double compensations) |
| **Durable Payment Idempotency & Fakes** | `src/Payments.Api/Application/` (`PaymentProcessing.cs`, `RefundProcessing.cs`, `ReconciliationHandler.cs`) | `PaymentWorkflowTests.cs` (`CompleteSuccessAndDifferentMessageDuplicatesHaveOneChargeInventoryAndSimulatedDelivery`) |
| **HTTP Resilience (Breaker, Budget, Limits)** | `src/Reservations.Api/Application/CatalogQuoteClient.cs`, `src/Shared.Infrastructure/Resilience/QuoteConcurrencyLimit.cs` | `QuoteResilienceTests.cs`, `OperationsTests.cs` (`SlowCatalogFiveFailuresOpenCircuitThenRecoveryCreatesWorkOnlyAfterQuoteSucceeds`) |
| **DLQ Inspection & Redrive Leases** | `src/Shared.Infrastructure/Operations/` (`DlqInspectionEndpoints.cs`, `DlqService.cs`) | `OperationsTests.cs` (`PoisonFiveReceivesCorrectionReplayAndSendDeleteFailureKeepOriginalIdentityAndOneProjectionEffect`) |
| **Defense-in-Depth Authentication & RBAC** | `src/Shared.Infrastructure/LabHosting.cs`, `src/Gateway.Web/Identity/` (`JwtIssuer.cs`, `LabRevalidatingAuthenticationStateProvider.cs`) | `SecurityTests.cs` (17 invalid token cases, ownership cross-checks), `tests/EndToEnd/security.spec.js` |
| **Blazor Interactive Circuit Scoping** | `src/Gateway.Web/Catalog/CatalogClient.cs`, `CatalogComponent.cs`, `src/Gateway.Web/Components/Pages/` | `CircuitIsolationTests.cs`, `tests/EndToEnd/security.spec.js` |
| **Distributed Tracing & Health Probes** | `src/Shared.Infrastructure/LabTelemetry.cs`, `LabHosting.cs`, `Messaging/MessageCodec.cs`, `Workers.cs` | `OperationsTests.cs`, `MessagingTests.cs` (`InvalidTraceparent` poison detection, live vs ready separation) |

---

## 3. Diagnostic Misconceptions Tracker

| Starting Idea | What is Correct | What is Incomplete / Incorrect | Architectural Reality in this Lab |
|---|---|---|---|
| **1. Sharing a DB undermines independence** | Sharing physical hardware introduces contention and blast radius risks. | Sharing a database instance with strict schemas and role grants is often a legitimate, cost-conscious operational stepping stone. | Separate schemas (`catalog`, `reservations`, etc.) with dedicated PostgreSQL users having DML only in their own schema prevents cross-querying while keeping hosting simple. |
| **2. Checking before inserting prevents duplicates** | Pre-checks catch many standard double-submits. | A "check then insert" has an inherent race condition (TOCTOU: Time of Check to Time of Use) under concurrent requests. | Only a unique database constraint / unique index (or atomic row lock) guarantees true idempotency under concurrency. |
| **3. Reservation and Payment in "the same transaction"** | Both must eventually agree on the business outcome. | In distributed microservices across independent processes/databases, a single ACID database transaction is impossible without 2PC (which destroys availability). | Eventual consistency via an asynchronous Saga state machine with compensatory actions (refunds, seat releases). |
| **4. Timeout means unknown outcome** | Exactly correct! A timeout only means the client did not hear back in time. | Assuming a timeout equals "failed" leads to double-charging or orphan reservations. | Timeouts are treated as `Unknown`. The system reconciles via stable idempotent operation IDs and status inquiries before retrying or refunding. |
| **5. API Gateway is sufficient security** | Gateway handles edge ingress, TLS termination, cookies, and coarse routing. | "Perimeter-only" security fails if network boundaries shift, misconfigurations occur, or internal services call each other. | Defense-in-depth: every microservice validates cryptographically signed JWTs, claims, lifetimes, roles, and resource ownership directly. |
| **6. Persisting Saga state for multiple replicas** | True, replicas cannot share in-memory state. | In addition, persistence is required for crash recovery, restart durability, and surviving network partition pauses. | Saga state is committed atomically to PostgreSQL alongside outbox messages; any replica can pick up the next event. |
| **7. Retry delays cause server bottlenecks** | Synchronous blocking (`Thread.Sleep`) occupies worker threads and exhausts thread pools. | Asynchronous delays (`Task.Delay` with timers) release threads back to the thread pool with near-zero overhead. | With `async/await`, threads are never blocked during backoff; the real concern is resource saturation at the downstream service (solved by circuit breakers and jitter). |
| **8. Liveness probe should verify the database** | Orchestrators need to know when containers are unhealthy. | Checking database connectivity in a liveness probe triggers a cascading restart storm during transient DB failovers or high CPU spikes. | Liveness (`/health/live`) strictly tests process responsiveness without touching dependencies; Readiness (`/health/ready`) verifies dependencies so load balancers shed traffic while containers stay alive. |

---

## 4. Glossary (Plain Language)

- **Bounded Context**: An explicit boundary within which a domain model applies. Inside Catalog, a "Tour" has sessions and seat capacities; inside Reservations, a "Tour" is merely an informational ID and title.
- **Idempotency Key / Intention Key**: A unique client-generated or saga-generated ID representing a single business intent. Resending the request with the same key returns the existing outcome without repeating the side effect.
- **Transactional Outbox**: Writing outgoing domain events/commands into an `Outbox` table in the *same local database transaction* as the entity state change, guaranteeing that if the business data commits, the outgoing intent is recorded.
- **Transactional Inbox**: Storing incoming message IDs in an `Inbox` table in the *same local transaction* as the message processing, guaranteeing at-least-once transport does not cause duplicate business execution.
- **Saga Orchestrator**: A centralized state machine (here, in `Reservations.Api`) that coordinates multi-step distributed workflows by dispatching commands and processing reply events.
- **Compensating Action**: An operation that semantically reverses a previously committed step (e.g. issuing a refund after payment, releasing reserved seats after payment decline).
- **Circuit Breaker**: A stability mechanism that stops sending requests to a failing service after a threshold of failures, failing fast to let the downstream service recover.
- **W3C Traceparent**: A standardized header format (`00-TraceId-SpanId-Flags`) passed across HTTP and message envelopes that connects distributed spans into a single unified trace waterfall.
- **Liveness vs. Readiness Probe**: Liveness determines whether a container process should be killed and restarted; Readiness determines whether a container should receive traffic from the load balancer.
- **Trace Sampling**: Techniques (head-based ratio or tail-based error sampling) to capture a representative percentage of traces without paying astronomical storage costs for millions of routine 200 OK requests.
