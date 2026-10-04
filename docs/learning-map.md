# Learning map: LAB-001 through LAB-004

Topics are applied examples, not declarations of mastery. Numbers follow specification 05.

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
