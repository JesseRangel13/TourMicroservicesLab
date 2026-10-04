# LAB-006 operations and recovery

Run from the repository root with PowerShell 7. Preserve `.local`, all database volumes and queues.

```powershell
./scripts/Start-Local.ps1
./scripts/Verify-Operations.ps1
./scripts/Verify-Local.ps1
./scripts/Verify-Sagas.ps1
./scripts/Verify-Messaging.ps1
```

`Verify-Operations` first exercises live workers and ten deterministic resilience cases, then pauses hosted workers for four controlled PostgreSQL/ElasticMQ checks and restores them in `finally`. Allow several minutes: TLS connections are validated and the real breaker needs its 30-second recovery interval. `-SkipLive` runs only controlled projection/replay/mode checks. Fault reset restores normal behavior; it never clears Outbox, Inbox, tables or queues. Test queues and records are retained as evidence.

Sign in as Admin at https://localhost:8443 and open **Lab operations** (`/operations`). The service selector uses authorized Gateway HTTP clients. Diagnostics displays one service's owned work counts, recent bounded Outbox/Inbox metadata and process-local worker observations. Each page contains ten rows by default (API maximum fifty). Last failure type is retained after recovery; Running means the worker's latest check completed, not that every business item succeeded. DLQ counts are approximate and include visible and invisible entries; -1 means the broker count was unavailable. Full payloads, provider receipts, credentials and broker receipt handles are omitted. Admin endpoints remain protected when called directly without Gateway; operational controls are limited to sixty requests/minute/user/instance. Existing provider/fault controls also retain their own limits.

**Slow Catalog and recovery.** Select Catalog, apply QuoteTimeout with 100 occurrences, then create five reservation intentions concurrently using API tools or the executable `SlowCatalogFiveFailuresOpenCircuitThenRecoveryCreatesWorkOnlyAfterQuoteSucceeds` test. Each quote attempt delays four seconds but the Reservations caller cancels it after three. Initial plus two attempts consume fifteen occurrences. Requests return 503 with CatalogAttemptTimeout and create no reservations/Sagas/payment work. The next request fails immediately with CatalogCircuitOpen. Reset Catalog, wait thirty seconds from the opening, then retry the same reservation idempotency key. One probe can succeed and reopen traffic. A single simulated occurrence may be recovered by a retry; use enough occurrences to exhaust attempts. Unavailable Catalog is covered separately by `Verify-Messaging` stopping/restarting its container. TotalTimeoutSeconds defaults to ten (validated 1–30); attempts remain three seconds. HttpClient itself has no competing timeout/retry handler. Caller cancellation propagates; codes distinguish total/attempt timeout, open circuit, busy and unavailable. Breaker and semaphore state belong to each Reservations process and reset on restart.

**Correlated workflow.** Complete a normal reservation, locate its reservation/Saga/operation IDs in business screens, then inspect JSON output:

```powershell
docker compose logs --no-log-prefix reservations payments catalog notifications |
    Select-String '"event":"span"|"event":"metric.delta"|Message committed'
```

HTTP spans share W3C trace context across Gateway, Reservations and Catalog. Outbox carries traceparent/tracestate; producer and consumer spans relate to that context. The handler is a child of the receive span. Persisted background payment/refund/sender work creates a new trace tagged with original business IDs; a long Saga can involve several traces. MessageId is not a metric label. Logs contain only safe identifiers, event/outcome and type names; never enable EF sensitive-data logging or HTTP header/body logging. The base JSON span exporter allowlists fields. OpenTelemetry 1.17.0 instruments ASP.NET and HttpClient and listens to the lab ActivitySource/Meter. Sampling defaults to 1 for this local learning session; `Telemetry__Sampling=0.1` reduces root traces while preserving sampled parent decisions. JSON metric deltas record counters; snapshots record owned work counts, Outbox count/age and approximate DLQ counts on diagnostics requests. Optional OTLP metrics export every sixty seconds using the SDK default. Metrics describe aggregates; logs explain occurrences; traces connect executions; persisted Saga history remains the source for business transitions.

**Optional tracing viewer.** Base Compose needs neither exporter endpoint nor tracing containers. This overlay sets plaintext OTLP on the isolated local Docker network; no TLS certificate validation is disabled for business HTTP or PostgreSQL.

```powershell
docker compose -f compose.yaml -f compose.tracing.yaml --profile tracing up -d
# Jaeger: http://localhost:16686 — select a service and find a reservation trace.
# Run a workflow and allow ten seconds for batching, then inspect actual exported spans:
./scripts/Verify-Tracing.ps1
docker compose -f compose.yaml -f compose.tracing.yaml --profile tracing stop otel-collector jaeger
docker compose up -d --wait gateway catalog reservations payments notifications
```

Jaeger holds at most 2,000 traces in memory and loses them on restart. Collector/viewer memory limits are 128/256 MiB. They publish only the viewer's loopback port and have no mandatory health dependency for business services. Stop them after a session. No CloudWatch backend, AWS collector or AWS tracing deployment is claimed.

**Projection.** Open `/tour-projections` as any authenticated user. Initial session creation/price changes persist Catalog's TourSessionChanged Outbox intent. The projection is absent until the first event commits in Reservations. Seed bootstrap publishes initial events; initialization is eventual and not an assertion that every Catalog record is copied. Replay old events leaves the highest PriceVersion intact; identical duplicates are safe. Same-version contradictory content is poison. The controlled test sends versions 1, 3, 2, 3 and a repeated MessageId through ElasticMQ and checks the query. Its informational price of 300 cannot authorize a reservation: Catalog still rejects it and accepts its actual quote price. Capacity never comes from this projection. Refresh shows version and receipt update time; no global polling runs.

**DLQ recovery.** Select the affected service and click **Receive up to 10**. This calls SQS ReceiveMessage, making inspected entries invisible for 120 seconds, with a 110-second backend inspection lease. Other operators may receive different entries or an empty batch; there is no SQL-style page/total guarantee. Review the redacted IDs/type, correct the consumer or configuration, then click **Replay original message** before expiry. Expired/in-progress/finished inspections return 409: receive again when visible. Bodies and receipt handles remain service-owned. Replay validates the destination against its owning queue, sends the exact original body and then requests DLQ deletion. Audit records the actor and result. SentDeleteUncertain requires checking the Inbox/business state before retrying; send could have succeeded even after a disconnect. A stale SQS receipt may not remove a message despite an accepted delete, so successful replay explicitly allows duplicates. Inbox and business IDs provide protection. No new MessageId, automatic infinite redrive, payload editor, purge or retention reset is provided. Invalid envelopes are inspectable but not replayable; contract migration requires a separate reviewed correction. The controlled poison test uses a valid projection envelope rejected by an explicitly simulated consumer configuration, corrects that boundary, and verifies one local effect despite duplicate send.

**Pause/reset.** Select Reservations and apply PauseOutbox with 100 occurrences. Refresh until worker status is Paused, create a reservation, and observe its durable pending HoldSeats intent. Reset, then observe that original delivery published. Catalog/Payments PauseConsumption delays receive checks without changing messages or accepted payment provider modes. Each pause occurrence is one one-second worker check; in-flight work can finish and broker long polling may take up to twenty seconds before checking a new mode. Existing confirmation/provider/sender modes remain available. LabFeaturesEnabled=false disables setting/resetting/consuming faults; diagnostics and authorized DLQ recovery remain independent operational capabilities.

Liveness performs no database or downstream call. Protected readiness checks only the service's runtime PostgreSQL connection when explicitly requested; Compose uses liveness. Snapshot queries have a five-second statement timeout and bounded pages; counting large lab tables still has a cost. Active workers already query their own pending state (Outbox/provider work about once/second; hold expiration every five seconds and Saga deadlines about once/second) and therefore an active session can keep Neon awake. Stop all application tasks between AWS sessions; fault pauses are recovery demos, not a cloud-cost pause. No background database metrics/readiness scraper was introduced. Automatic operational screen polling is intentionally absent; requests are cancellable and limited to 15/30 seconds.

ElasticMQ verifies local redrive, visibility, identity and duplicate handling. It does not prove AWS IAM, eventual approximate counts, stale receipt behavior under replicas, regional throttling, retention boundaries or ECS shutdown. Validate these during a separately authorized AWS task. Browser click-through and multi-user Interactive Server circuits remain LAB-007 checks; the executable HTTP and real persistence/broker tests do not replace browser E2E.

Malformed-envelope rows use an opaque inspection key in the delivery column and are explicitly non-replayable; this is not a replacement integration MessageId. No handler or replay silently fills missing business identifiers. The delete-outage check uses real SDK sends/receives and an explicitly simulated single SDK DeleteMessage failure; it does not claim to cause an actual AWS outage.
