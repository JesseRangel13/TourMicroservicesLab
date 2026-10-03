# Learning map: LAB-001 and LAB-002

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

Saga, Inbox, transport dispatch, and resilience await their implementation tasks. No fabricated code
examples are listed for them. A Money struct is not required to enforce this task's integer amount
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
