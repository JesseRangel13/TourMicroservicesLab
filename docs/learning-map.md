# LAB-001 learning map

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

Money structs, handlers/CQRS, Saga, Outbox/Inbox, resilience, and business idempotency await their
implementation tasks. No fabricated code examples are listed for them. Covariance, ref/out,
hand-built expression trees, domain events, and in-process locks are not needed by this foundation.
EF model building does not require a custom reflection dispatcher. OpenTelemetry and business
correlation are planned for LAB-006; baseline logs/errors already expose a traceId safely.

Gateway has one replica in this lab. Interactive Server circuits have in-process state; adding replicas
requires a decision about affinity/transport before deployment. Identity + a JWT issuer does not
implement OIDC discovery, authorization code flow, or PKCE.
