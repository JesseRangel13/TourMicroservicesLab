# Learning map: LAB-001 through LAB-007

Topics are applied examples, not declarations of mastery. Numbers follow specification 05.

## Current roadmap coverage

The package references `docs/roadmap.md`, but that file is absent from this repository. The actual seventeen numbered topics below follow `specs/05-blazor-and-learning.md`; no additional topic numbers are invented. Historical staged examples below describe their original task, not today's implementation boundary.

| Topic | Concrete implementation path | Why useful here | Short exercise | Interview question |
|---|---|---|---|---|
| 1 Value/reference/parameter passing | `src/Payments.Api/Persistence/PaymentsDb.cs`; `src/Shared.Contracts/PaymentContracts.cs` | Tracked mutable work is distinct from a returned immutable snapshot. | Change an entity after projecting a PaymentView; inspect both values. | When does passing a reference permit mutation without replacing the caller's variable? |
| 2 Class/struct/record | `src/Gateway.Web/Identity/LabUser.cs`; `src/Shared.Contracts/CatalogContracts.cs` | Identity/EF entities are classes; transport/value snapshots are records. Money remains checked integer minor units. | Compare two equal CreateReservation records with two entity instances. | Why avoid record equality for tracked mutable entities? |
| 3 Equality | `src/Shared.Infrastructure/Messaging/MessageCodec.cs`; `src/Catalog.Api/Application/InventoryHandlers.cs` | Canonical payload hashes and stable operation IDs detect repeats/conflicting content. | Repeat an operation with changed participants and inspect its conflict. | Why is MessageId equality alone insufficient for payment idempotency? |
| 4 Nullable references | `Directory.Build.props`; `src/Payments.Api/Application/PaymentProcessing.cs` | Missing provider lookup results represent uncertainty, not decline. | Follow the nullable lookup path to Unknown and reconciliation. | Why is null not evidence that no charge occurred? |
| 5 Generics/constraints/variance | `src/Shared.Contracts/CatalogContracts.cs` IntegrationEnvelope<T>; typed clients' SendAsync<T> | Typed payloads and responses reduce casts without a dispatch framework. | Deserialize the wrong payload type and observe validation. | When would variance solve an actual boundary problem? |
| 6 Delegates/lambdas/closures/events | `src/Gateway.Web/Program.cs`; `src/Shared.Infrastructure/LabHosting.cs` | DI/Options/middleware callbacks bind actual policy and resource lifetimes. | Trace what the JWT Options callback captures and who owns RSA disposal. | How do integration events differ from in-process C# events? |
| 7 LINQ/IEnumerable/IQueryable | `src/Reservations.Api/Application/ReservationHandlers.cs` ReservationQueries | Ownership filtering/pagination execute at PostgreSQL, before materialization. | Compare an IQueryable filter with a filter after ToArrayAsync. | Why can early materialization leak data and waste work? |
| 8 Exceptions | `src/Shared.Infrastructure/LabHosting.cs` SafeExceptionHandler; owned endpoint filters | Expected conflicts have stable codes; unexpected failures expose no secret messages. | Trigger VersionConflict and compare its response with an unexpected failure. | Which failures belong in domain results versus middleware? |
| 9 Async/await | `src/Shared.Infrastructure/Messaging/Workers.cs`; `src/Gateway.Web/Components/Pages/ReservationDetail.razor` | Cancellation reaches I/O; owned tasks are observed/joined. | Run ComponentDisposalCancelsOutstandingIoAndRejectsQueuedActions. | Why does cancelling a caller not undo a committed provider effect? |
| 10 DI/IoC/lifetimes/factories/Options | `src/Gateway.Web/Identity/IdentityRegistration.cs`; `src/Payments.Api/Application/PaymentWorker.cs`; `src/Reservations.Api/ReservationEndpoints.cs` | Short contexts, scoped work and validated bounded configuration prevent captive state. | Identify singleton → async scope → DbContext in a worker. | Why does a Blazor circuit scope differ from an HTTP request scope? |
| 11 ASP.NET pipeline/APIs | `src/Gateway.Web/Program.cs`; `src/Shared.Infrastructure/LabHosting.cs`; owned endpoints | Authentication precedes authorization/limiting; cookie and bearer boundaries stay explicit. | Trace a tourist Admin mutation to 403 and a foreign detail to 404. | Why is an Admin AuthorizeView insufficient? |
| 12 EF/SQL | `src/Catalog.Api/Application/InventoryHandlers.cs`; owned `Persistence/Migrations/`; `tools/Lab.Provisioner/DatabaseBootstrap.cs` | Atomic inventory and role grants are database invariants, not search_path conventions. | Run the real runtime-role isolation check and final-seat test. | Which constraints survive a second application replica? |
| 13 Testing/refactoring | `tests/Lab.Tests/SecurityTests.cs`; `CircuitIsolationTests.cs`; `tests/EndToEnd/security.spec.js`; `.github/workflows/ci.yml`; `scripts/Verify-CI.ps1`; `scripts/Test-CIReports.ps1` | Failure-oriented tests run in isolated CI; redacted reports preserve failed outcomes without sensitive diagnostics. | Explain the worker gates, intentional skips and report allowlist. | Why do Windows local checks and a Linux GitHub run establish different evidence? |
| 14 Concurrency/resources | `src/Reservations.Api/Application/CatalogQuoteClient.cs`; `src/Payments.Api/Application/RefundProcessing.cs` | Semaphore/breaker synchronization and durable leases/version fences solve different concurrency scopes. | Race two claims and inspect owner/version fencing. | Why cannot a process lock provide cross-replica inventory safety? |
| 15 Architecture/SOLID/CQRS | `src/Reservations.Api/Application/SagaCommands.cs`; `src/Payments.Api/Application/PaymentProcessing.cs`; owned query handlers | Concrete typed handlers separate work; provider/transport interfaces are replaceable boundaries. | Replace IPaymentProvider with a deterministic timeout fixture. | What is the cost of five services compared with a modular monolith? |
| 16 Security/distributed reliability | `src/Shared.Infrastructure/LabHosting.cs`; `src/Shared.Infrastructure/Messaging/OutboxDispatch.cs`; `src/Gateway.Web/Identity/LabRevalidatingAuthenticationStateProvider.cs` | Required identity claims, ownership and durable intent/effect identity solve separate risks. | Run direct invalid-JWT, cross-owner and timeout-after-charge checks. | What can logout revoke immediately, and what waits for revalidation/JWT expiry? |
| 17 Docker/ECS/observability | `Dockerfile`; `compose.yaml`; `src/Shared.Infrastructure/LabTelemetry.cs`; `compose.tracing.yaml` | Independent images, session limits and safe trace IDs make the local system observable. ECS remains pending. | Stop/start without deleting volumes and compare encrypted-cookie/provider evidence. | Why do local image builds not prove AWS IAM, cost or Neon suspension? |

### Concepts not needed, and comparison topics

- `ref`/`out`, extra generic constraints, covariance/contravariance interfaces and hand-built expression trees: **not needed**. Immutable requests/returned results and concrete typed boundaries suffice; typed JSON responses need no artificial constraint, and EF already translates IQueryable expressions.
- A Money struct: **not needed** for this single-currency lab's validated integer amounts and checked multiplication. An exercise is to explain what a multiple-currency value object would add before introducing one.
- In-process domain/C# events, a generic repository and a reflection dispatcher: **not needed**. Explicit handlers plus durable Outbox/Inbox own each transaction. Process synchronization IS used naturally in the quote limiter; it is not claimed to protect distributed business state.
- OAuth/OIDC/PKCE: **comparison only**. Identity and this lab JWT issuer do not implement them. Exercise: outline discovery/authorization-code/PKCE responsibilities of an external provider; question: what would migrate out of Gateway?
- MediatR: **not needed**. Exercise: follow a typed command directly; question: which real repetition would justify a dispatcher library?
- SOAP: **not needed** for the HTTP/JSON contracts. Exercise/question: what compatibility constraints would motivate a SOAP boundary adapter?
- Jenkins/Sonar: **not implemented**; local scripts provide reproducible checks. Exercise/question: how would a CI pipeline protect private configuration and run the real database suites?
- Strangler: **comparison only**; this is a student greenfield lab. Exercise/question: describe incremental routing/data ownership when replacing a legacy booking system.

No topic is marked mastered. Browser runtime proof and AWS readiness remain separate evidence obligations.

## Historical LAB-001 examples

| Topic | Concrete implementation | Exercise / interview question |
|---|---|---|
| 2 Classes/records | `Identity/LabUser.cs` mutable Identity entity; `Shared.Contracts/ServiceStatus.cs` transport record | Why does record value equality not identify a mutable database row? |
| 4 Nullability | `Directory.Build.props`; nullable credential DTO; `RuntimeDatabaseOptions.cs` | Why reject missing config instead of silencing a nullable warning with `!`? |
| 6 Delegates/lambdas | credential rate-limit partition and service configuration in `Gateway.Web/Program.cs` | What state does each callback capture? Why are C# events unsuitable for inter-service delivery? |
| 7 LINQ | seed lookup and role claim projection in `AuthEndpoints.cs` and provisioner | Distinguish in-memory claim projection from an EF IQueryable query. |
| 8 Exceptions | `Shared.Infrastructure/LabHosting.cs` SafeExceptionHandler | Which errors are expected 400/401/403, and why never log a connection exception's raw text? |
| 9 Async/cancellation | bootstrap SQL/migrations pass cancellation; HTTP form reads and readiness honor request cancellation | UserManager public operations lack a cancellation parameter; how would you assess a custom store boundary if stronger cancellation were needed? |
| 10 DI/lifetimes/factory/Options | `IdentityRegistration.cs` AddDbContextFactory; revalidation's async scope; `RuntimeDatabaseOptions.cs` ValidateOnStart | Why must a circuit not retain a DbContext? What prevents a privileged connection being supplied at runtime? |
| 11 Pipeline/APIs | `Gateway.Web/Program.cs`; `AuthEndpoints.cs`; four API Program files | Trace login → secure cookie → Blazor circuit versus tool credentials → JWT → YARP → API. |
| 12 EF/SQL | `Identity/Migrations/`; `DatabaseBootstrap.cs`; runtime permission integration test | Why does search_path alone fail to isolate schemas? Why migrate before hosts start? |
| 13 Tests | `tests/Lab.Tests/JwtValidationTests.cs`, `IntegrationTests.cs`, `PersistenceTests.cs` | Which bug would an in-memory database miss? Why is a fake-provider test not relevant until LAB-004? |
| 14 Resources | async-disposed SQL connections/scopes; short revalidation scope | Who disposes a factory-created context versus a DI-scoped context? |
| 15 Boundaries | five hosts; two infrastructure-only shared projects; separate provisioner | Why avoid a generic repository/interface for every class in this scaffold? |
| 16 Security | per-service JWT validation, SSR antiforgery, local CA, encrypted Data Protection | Explain the difference between cookie/session identity and stateless JWT expiry. |
| 17 Containers | `Dockerfile`, `compose.yaml`, pinned tags/digests/lock files, stop/start checks | Why do stopping containers and deleting volumes have different durability outcomes? |

Complete Saga recovery and resilience await their implementation tasks. A Money struct is not required to enforce this task's integer amount
boundary and checked multiplication. Covariance, ref/out,
hand-built expression trees, domain events, and in-process locks are not needed by this foundation.
EF model building does not require a custom reflection dispatcher. OpenTelemetry and business
correlation are planned for LAB-006; baseline logs/errors already expose a traceId safely.

Gateway has one replica in this lab. Interactive Server circuits have in-process state; adding replicas
requires a decision about affinity/transport before deployment. Identity + a JWT issuer does not
implement OIDC discovery, authorization code flow, or PKCE.

## LAB-002 concrete examples

| Topic | Actual code paths | Exercise / interview question |
|---|---|---|
| 1–3 Classes, records, equality | `src/Catalog.Api/Persistence/CatalogDb.cs`; `src/Shared.Contracts/CatalogContracts.cs` | Explain why a SeatHold row uses a stable ID while a CreateQuote record compares content. Why is an immutable quote not versioned like a mutable session? |
| 4 Nullability | nullable HTTP DTO inputs; `CatalogRules.Text`; nullable tombstone metadata in `CatalogDb.cs` | Distinguish an unknown-before-hold release from a corrupted held row. Explain validated null-forgiving uses at boundaries. |
| 5 Generics | `IntegrationEnvelope<T>`; `CatalogRules.AddIntentAsync<T>`; `CatalogClient.SendAsync<T>` | What useful type information survives without a reflection dispatcher or MediatR? |
| 7 LINQ/SQL | `CatalogQueries.SearchAsync` in `Application/CatalogOperations.cs` | Trace filters, count, ordering and pagination to database execution. Where does IQueryable become materialized? |
| 8 Expected errors | `CatalogProblem`, `CatalogEndpoints.MapCatalog`, `Shared.Infrastructure/LabHosting.cs` | Why preserve explicit ProblemDetails codes? How do business conflicts differ from unexpected exceptions? |
| 9 Async/cancellation | all Catalog handlers; `Gateway.Web/Catalog/CatalogComponent.cs` | Cancellation disposes a screen's request lifetime. Why must an ambiguous HTTP timeout not automatically repeat a create? |
| 10 DI/Options/lifetimes | `CatalogEndpoints.AddCatalog`; Gateway Program's GatewayApiOptions; scoped CatalogClient | API DbContext is request-scoped; client is circuit-scoped and owns no DbContext. Why must HttpClient handlers not capture the circuit principal? |
| 11 Pipeline/APIs | `CatalogEndpoints.cs`, Gateway route blockers and endpoint filter | Trace cookie → AuthenticationStateProvider → per-request JWT → Gateway bearer policy → Catalog AdminApi. Why is hiding a button insufficient? |
| 12/14 EF, transactions, concurrency | `Application/InventoryHandlers.cs`; `Persistence/Migrations/`; `CatalogTests.cs` | Explain HoldId advisory lock → Session row lock → conditional decrement → hold + Outbox commit. Explain confirmation at exactly its deadline and version conflicts. |
| 13 Tests | `CatalogTests.cs`, `CatalogHttpTests.cs`, `CatalogContractTests.cs` | Which checks require PostgreSQL? Distinguish direct handler concurrency, real HTTPS/SSR checks and an interactive browser workflow. |
| 15 CQRS/boundaries | `CatalogQueries`, `CatalogCommands`, `QuoteHandler`, `InventoryHandlers` | Why concrete typed handlers and direct EF queries solve this task without a generic repository or domain event bus? |
| 16 Security/idempotency | dedicated service JWT policy; immutable quotes; HoldId fingerprints/tombstones; unique Outbox effect key | Distinguish request identity, operation identity and delivery identity. Why does a release arriving first require durable state? |
| 17 Local execution | `DatabaseBootstrap.cs`, `LocalConfiguration.cs`, `scripts/Start-Local.ps1` | Why do migrations need different credentials? Why does durable Outbox data not prove message delivery? |

TimeProvider is the replaceable clock boundary. Tests advance it instead of waiting five/ten minutes.
Amounts remain long integer cents; decimal is used only to format displayed MXN amounts. Projection
serialization tests preserve integers beyond double precision. No learning topic is marked mastered.

## LAB-003 concrete examples

| Topic | Actual code paths | Exercise / interview question |
|---|---|---|
| Classes/records/nullability | `src/Reservations.Api/Persistence/ReservationsDb.cs`; `src/Shared.Contracts/ReservationContracts.cs`; `src/Shared.Infrastructure/Messaging/MessageCodec.cs` | Why are tracked mutable entities classes while commands and envelopes are records? Why must malformed envelopes fail at the boundary? |
| Async/cancellation/resources | `src/Shared.Infrastructure/Messaging/Workers.cs`; `src/Gateway.Web/Components/Pages/ReservationDetail.razor` | Trace shutdown through receive, visibility renewal, transaction and acknowledgement. Who observes and joins polling/renewal tasks? |
| DI/Options/lifetimes | `src/Reservations.Api/ReservationEndpoints.cs`; `src/Shared.Infrastructure/Messaging/SqsTransport.cs`; `src/Reservations.Api/Application/CatalogQuoteClient.cs` | Singleton transport/issuer, scoped consumers/DbContext, typed HttpClient: explain each lifetime and why workers create async scopes. |
| HTTP/security | `src/Gateway.Web/Reservations/ReservationClient.cs`; `src/Reservations.Api/ReservationEndpoints.cs`; `CatalogQuoteClient.cs` | Distinguish the user's per-request JWT from the dedicated service identity. Why is UserId derived from validated claims? |
| Transactions/idempotency | `src/Reservations.Api/Application/ReservationHandlers.cs`; `src/Shared.Infrastructure/Messaging/LocalTransactions.cs`; `src/Catalog.Api/Application/InventoryHandlers.cs` | Explain HTTP before transaction, unique-conflict rollback before reread, and why nested inventory handlers join the Inbox transaction. |
| Durable delivery/leases | `src/Shared.Infrastructure/Messaging/OutboxDispatch.cs`; `Workers.cs`; `tests/Lab.Tests/MessagingTests.cs` | Explain send-before-mark duplicates, consumer-commit-before-delete recovery, lease fencing and stable operation/message/delivery identities. Why is delivery not exactly once? |
| Typed handlers/boundaries | `src/Catalog.Api/Messaging/CatalogConsumer.cs`; `src/Reservations.Api/Messaging/ReservationConsumer.cs` | Follow explicit routing and typed payload dispatch without shared entities, another service's tables or a reflection dispatcher. |
| Evidence/testing | `tests/Lab.Tests/ReservationHttpTests.cs`; `MessagingTests.cs`; `MessagingContractTests.cs` | Separate pure envelope checks, direct PostgreSQL handlers, real SDK transport crash windows and live HTTP-to-SQS workflow tests. |

Activity trace context and durable correlation exist now; full telemetry, retry/breaker policies and
DLQ administration remain LAB-006. Refund, cancellation and complete compensation handlers
remain later tasks. Browser circuit isolation remains LAB-007. No topic is marked mastered.

## LAB-004 concrete examples

| Topic | Actual code paths | Exercise / interview question |
|---|---|---|
| Classes/records/nullability | `src/Payments.Api/Persistence/PaymentsDb.cs`; `src/Notifications.Api/Persistence/NotificationsDb.cs`; `src/Shared.Contracts/PaymentContracts.cs` | Why are missing provider results nullable, and why does Unknown never imply no charge? |
| Async/resources/lifetimes | `src/Payments.Api/Application/PaymentWorker.cs`; `src/Notifications.Api/Application/NotificationWorker.cs`; owned processors/providers | Explain the async scope per item, forty-second bound and separate factory-created effect contexts. |
| Replaceable boundaries/DI | `IPaymentProvider`, `DurableFakeProvider` in `PaymentProcessing.cs`; `INotificationSender`, `DurableFakeSender` in `NotificationProcessing.cs` | Which boundaries justify an interface? Why are simple processors concrete? |
| EF transactions/concurrency | `PaymentProcessor.ProcessAsync`; `NotificationProcessor.ProcessAsync`; owned migrations | Explain claim transaction → independent effect commit → fenced result transaction. Which crash remains visible? Why are in-memory locks insufficient? |
| Durable idempotency/equality | `PaymentConsumer`, provider RequestHash, notification ReservationId/Kind uniqueness and sender SourceEventId/Kind receipt | Distinguish message deduplication, payment operation identity and terminal business-notice identity. |
| Pipeline/security/typed clients | `PaymentEndpoints.cs`, `NotificationEndpoints.cs`; `src/Gateway.Web/Business/BusinessClient.cs` | Trace cookie → circuit principal → per-request JWT → Gateway → ownership/Admin policies. Explain bounded, audited and rate-limited lab controls. |
| State transitions/CQRS | `src/Reservations.Api/Messaging/ReservationConsumer.cs`; owned API query projections | Why must a successful payment wait for Catalog confirmation? Why does decline remain Compensating until release? |
| Tests/recovery | `tests/Lab.Tests/PaymentWorkflowTests.cs`, `ReservationHttpTests.cs`, `PersistenceTests.cs` | Separate real transport controlled-worker tests, enabled-host HTTPS workflow, independent-effect crashes and actual Compose restart recovery. |
| Cancellable UI polling | `src/Gateway.Web/Components/Pages/Payments.razor`, `Notifications.razor`, `ReservationDetail.razor` | Who owns/observes polling tasks? Why should lists or inactive screens avoid background polling? |

No fake-provider outcome uses randomness or an authoritative volatile dictionary. Integer positive
MXN amounts remain the money boundary. Refund methods reject unsupported execution instead of
returning a fictitious refund. Complete compensation is LAB-005; no generic repository, event bus,
dispatcher library or additional infrastructure was added. No topic is marked mastered.

## LAB-005 concrete examples

| Topic | Actual code paths | Exercise / interview question |
|---|---|---|
| 2/4 Classes, records, nullable outcomes | `src/Payments.Api/Persistence/RefundOperation.cs`; `src/Shared.Contracts/PaymentContracts.cs` | Distinguish mutable durable work from immutable payloads. Why does a nullable refund lookup mean unresolved work rather than a failed refund? |
| 9/10 Async, cancellation, scopes, Options | `src/Reservations.Api/Application/SagaRecovery.cs`; `src/Payments.Api/Application/RefundProcessing.cs`; `PaymentWorker.cs` | Trace cancellation through scoped work, independent provider context and result persistence. Who discovers work after process restart? Validate bounded deadline/lookup settings. |
| 11/16 Pipeline and security | `src/Reservations.Api/ReservationEndpoints.cs`; `SagaCommands.cs`; `src/Payments.Api/PaymentEndpoints.cs`; `src/Catalog.Api/CatalogEndpoints.cs` | Explain owner/Admin authorization, 404 isolation, mandatory cancellation key, lab enablement, rate limiting and provider-only resolution auditing. |
| 12/14 EF, transactions, concurrency | `src/Reservations.Api/Messaging/ReservationConsumer.cs`; `SagaRecovery.cs`; `src/Payments.Api/Application/RefundProcessing.cs` | Explain Saga row locks plus Version tokens; independent provider commit; leased claims and owner fencing. Race refund/release results with separate contexts. |
| 13 Failure-oriented tests | `tests/Lab.Tests/SagaWorkflowTests.cs`; `PersistenceTests.cs`; `scripts/Verify-Sagas.ps1` | Compare real HTTPS/transport flows, a post-Failed recovery-handler fixture, deterministic clock tests and actual Compose restart. Which layer does each result prove? |
| 15 CQRS and explicit recovery | `src/Reservations.Api/Application/SagaCommands.cs`; `SagaRecovery.cs`; `src/Payments.Api/Application/RefundProcessing.cs` | Why do cancel commands accept durable work rather than perform cross-service effects inside the HTTP transaction? Why no generic repository or dispatcher library? |
| 16 Durable idempotency and uncertainty | `RefundAcceptance`; `DurableFakeProvider.RefundAsync`; `SagaRecovery.CompleteAsync` | Separate MessageId from RefundOperationId, original charge identity and terminal notice identity. Explain why uncertain payments block terminal failure. |
| 5/7/9 Typed UI clients and resources | `src/Gateway.Web/Business/BusinessClient.cs`; `Reservations/ReservationClient.cs`; `Components/Pages/Sagas.razor`; `ReservationDetail.razor`; `Payments.razor` | Explain per-request principal/JWT, paginated projections, intention keys and joined cancellable polling. No DbContext belongs to a circuit. |

### Five interview answers grounded in LAB-005

1. **Why can a payment timeout not be treated as a decline?** The provider may commit before the
   caller observes its response. `DurableFakeProvider` and `PaymentProcessor` use separate transactions;
   Unknown leads to lookup by the same operation ID, never a second charge.
2. **How do you avoid a second refund after a crash?** `RefundProcessor` first calls status lookup.
   ProviderRefunds has a unique original-payment link and request hash; a committed provider effect
   survives an application-result crash. Leases fence competing application workers.
3. **Why are compensation flags independent?** Refund and release have different owners and transactions.
   `SagaRecovery.CompleteAsync` waits for both required confirmations, regardless of arrival order;
   neither a sent command nor a timeout proves completion.
4. **How do multiple replicas handle a deadline/result race?** Both acquire the Saga row lock and
   reload state before deciding. Version is checked on writes. A confirmation that wins first remains
   Confirmed; abandonment that wins first prevents late confirmation from resurrecting success.
5. **What does ManualReview mean?** Automatic recovery could not verify completion within its bounded
   window. Admin changes only the fictional provider outcome or retries missing compensation with
   original IDs. It is an operational obligation, not a successful refund or proof of no charge.

Historical LAB-003/004 sections describe their staged boundaries. Current LAB-005 supplies refund,
cancellation and deadline recovery. LAB-006 now supplies diagnostics/observability; automated browser
circuit isolation remains LAB-007. No learning topic is automatically marked mastered.

## LAB-006: bounded operations and observability

| Actual code | Concepts | Exercise / interview question |
|---|---|---|
| `src/Reservations.Api/Application/CatalogQuoteClient.cs` | HttpClientFactory; asynchronous cancellation; total/attempt budgets; SemaphoreSlim; thread-safe consecutive breaker; per-instance DI lifetime | Explain why retry limits alone do not bound duration. Run QuoteResilienceTests and identify which failures qualify. Why does an old completion need a generation fence? |
| `src/Shared.Infrastructure/LabTelemetry.cs`, `Messaging/Workers.cs`, `Messaging/OutboxDispatch.cs` | ActivitySource/ActivityContext; OpenTelemetry; JSON allowlists; traces vs history; low-cardinality Meter labels | Follow actual message TraceId/ParentSpanId in `.local/lab006-message-traces.json`. Explain why a Saga can outlive one trace and why ReservationId belongs in spans, not metric labels. |
| `src/Shared.Infrastructure/Operations/ServiceOperations.cs` and each service's `Lab006Operations` migration | Service-owned SQL plumbing; bounded queries; Admin endpoint authorization; leasing; durable audit; send-before-delete | Run the duplicate replay and expired inspection tests. Why can a successful SQS DeleteMessage still leave a future delivery? Why is DLQ inspection a mutation of broker visibility? |
| `src/Reservations.Api/Messaging/ReservationConsumer.cs`, `ReservationEndpoints.cs` | Transactional Inbox; projection versions; appropriate immutable DTO records; service-owned EF models | Send 3 then 2 and prove the projection remains at 3. Explain why this copy cannot approve a price or allocate seats. |
| `src/Gateway.Web/Business/BusinessClient.cs`, `Components/Pages/Operations.razor`, `TourProjections.razor` | Per-user request authorization; cancellable UI calls; typed contracts; manual refresh; disposable resources | Switch the selected service and inspect network activity. No inactive-service polling should occur. Browser circuit isolation is still a LAB-007 verification exercise. |
| `compose.tracing.yaml`, `infra/local/otel-collector.yaml`, `docs/operations-execution.md` | Optional infrastructure; sampling; memory limits; liveness/readiness; session cost discipline | Compare base JSON output with Jaeger, then stop tracing containers. Explain why an active worker session can keep Neon awake even when readiness has no periodic scraper. |

1. **Why not use the standard ratio breaker?** The contract requires consecutive logical failures. The concrete breaker implements exactly that policy; retries remain inside each logical operation. A successful/non-transient response clears consecutive failures, while caller cancellation does not count as dependency failure.
2. **What makes replay safe?** Original message/business IDs, own-queue validation, backend leases and existing Inbox/business idempotency. Sending precedes deletion; an uncertain boundary may duplicate delivery. Safety concerns effects, not exactly-once transport.
3. **What is the difference between health and diagnostics?** Liveness measures process availability without dependencies; readiness verifies the owned database on demand; diagnostics explains current work/worker observations through protected bounded APIs. Neither requires all downstream services.
4. **How do you keep telemetry useful and private?** Explicit safe identifier scopes, disabled sensitive EF/header/body logging, allowlisted JSON spans, and bounded metric labels. Business history remains a persisted queryable domain record; traces/logs are diagnostic evidence.
5. **Why no interface for every new class?** The quote client and transport are replaceable boundaries. A concrete breaker, worker-status store and SQL operations helper implement simple plumbing. EF models stay owned; no generic repository, reflection dispatch or new architecture layers were needed.
