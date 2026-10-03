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
