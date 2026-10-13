# Learn C# through Tour Microservices

This course explains the current project in English, from language fundamentals to distributed recovery. Lesson numbers below are study sessions, not replacements for the seventeen roadmap topic numbers in `specs/05-blazor-and-learning.md`. The referenced `docs/roadmap.md` is absent; this course uses the checked-in specifications and implementation.

Read one lesson at a time. For each example, explain what each line does, what failure it prevents, and which test could challenge it. Code blocks marked **teaching example** illustrate existing concepts; they are not new application implementations. Short repository excerpts may omit surrounding validation.

The repository targets .NET 10, enables nullable reference types, treats warnings as errors, and pins SDK 10.0.401. This document is based on source inspection. No application, build, browser, database, or cloud checks were executed for this teaching task. Named tests are existing evidence locations, not claims of fresh passing results. Existing evidence and its limitations remain in `docs/evidence/` and `docs/acceptance-matrix.md`.

**Course sequence**

1. System map and one booking
2. Types, variables, money, and time
3. Classes, objects, references, and parameter passing
4. Records, equality, and snapshots
5. Nullability and validation
6. Methods, constructors, inheritance, and interfaces
7. Generics and JSON contracts
8. Lambdas, delegates, closures, and events
9. LINQ and database query execution
10. Exceptions and expected business failures
11. Async, tasks, and cancellation
12. Dependency injection, lifetimes, factories, and Options
13. ASP.NET Core startup, middleware, and endpoints
14. EF Core, SQL, migrations, and transactions
15. Concurrency, locks, versions, leases, and disposal
16. CQRS, service boundaries, and SOLID
17. Idempotency and stable identifiers
18. Outbox, Inbox, queues, and DLQs
19. Sagas, deadlines, and compensation
20. Durable simulated providers and HTTP resilience
21. Identity, JWTs, authorization, and TLS
22. Blazor components, circuits, and user intentions
23. Image storage and DynamoDB projections
24. Tests, containers, telemetry, and practice

**Lesson 1 — System map and one booking**

A microservice is a separately running application responsible for a business area. A folder or class alone does not make a microservice. This repository has five hosts:

| Host | Responsibility | Owned PostgreSQL schema |
|---|---|---|
| Gateway.Web | Blazor UI, Identity, and YARP routing | identity |
| Catalog.Api | Tours, sessions, authoritative quotes, seats, and image metadata | catalog |
| Reservations.Api | Reservations, Saga coordination, and booking projections | reservations |
| Payments.Api | Durable simulated charges and refunds | payments |
| Notifications.Api | Durable simulated delivery | notifications |

They share one physical PostgreSQL database for this lab, with separate schemas and restricted roles. Ownership means each service controls its own tables and business decisions. Reservations cannot obtain seats by updating Catalog's tables.

`Shared.Contracts` carries integration and HTTP data shapes. `Shared.Infrastructure` provides reusable hosting, messaging, and diagnostics plumbing. Neither is a shared business database model. `Lab.Provisioner` performs privileged bootstrap separately from runtime hosts.

Follow one reservation:

```mermaid
sequenceDiagram
    participant U as Blazor user
    participant R as Reservations
    participant C as Catalog
    participant P as Payments
    participant N as Notifications
    U->>R: Accepted quantity/price + idempotency key
    R->>C: Authenticated quote request over HTTPS
    C-->>R: Valid quote
    R-->>U: 202 after durable reservation commit
    R->>C: HoldSeats through Outbox and queue
    C->>R: SeatsHeld
    R->>P: ProcessPayment
    P->>R: PaymentSucceeded, simulated durable charge
    R->>C: ConfirmSeats
    C->>R: SeatsConfirmed
    R->>N: ReservationConfirmed
    Note over N: Durable simulated delivery; no real email
```

The asynchronous arrows travel through persisted Outbox records and queues. HTTP 202 means work was accepted durably; the reservation can still fail later. Confirmed requires both a successful simulated charge and actual Catalog seat confirmation.

Read `README.md`, `specs/02-architecture.md`, and `specs/04-workflows.md`. Exercise: explain which service can approve a price, allocate a seat, and finish a reservation.

**Lesson 2 — Types, variables, money, and time**

A type defines the values a variable can hold and the operations available on them. Your project naturally uses `int` for participants, `long` for amounts and versions, `bool` for flags, `string` for descriptions, `Guid` for identifiers, and `DateTimeOffset` for timestamps.

Money is stored as integer minor units. `12345` means MXN 123.45. This makes the accepted amount explicit without binary floating-point rounding. The application validates positive amounts and MXN currency.

```csharp
// Teaching example using the project's money convention.
long unitAmountMinor = 12345;
int participants = 2;
long totalAmountMinor = checked(unitAmountMinor * participants);
decimal displayAmount = totalAmountMinor / 100m;
```

`checked` makes integer overflow throw rather than silently wrap. `100m` is a decimal literal used for display. The underlying accepted amount remains the integer 24690. `var` means compile-time type inference, not a dynamically typed variable.

UTC timestamps support consistent deadline comparisons across services. `TimeProvider` is a replaceable clock; tests can advance it without waiting ten real minutes. A `Guid` identifies an operation or resource; an ID by itself proves neither ownership nor permission.

Read `QuoteHandler` in `src/Catalog.Api/Application/CatalogOperations.cs`. Exercise: calculate the amount for three participants and explain why a positive unit amount alone does not prevent multiplication overflow.

**Lesson 3 — Classes, objects, references, and parameter passing**

A class defines a shape and behavior. An object is one instance. `Tour` and `Reservation` are mutable classes owned by their services. Properties such as `Name { get; set; }` allow reading and changing an object's data.

```csharp
// Teaching example; Tour exists in CatalogDb.cs.
var first = new Tour { Id = Guid.NewGuid(), Name = "City tour" };
var second = first;
second.Name = "Walking tour";
// first.Name is now also "Walking tour".
```

Both variables hold references to the same object. Assignment copied the reference, not the object. In contrast, assigning a `long` or `Guid` copies its value.

Parameters are passed by value by default. For a class parameter, the copied value is a reference. A method can mutate the referenced object's properties, but reassigning that parameter to a different object does not replace the caller's variable. `ref` would change that rule; this project does not need it to process commands.

`new Tour { ... }` is an object initializer. It creates an object and sets properties. `private` members hide internal details; `public` members expose operations callers can use. Encapsulation also depends on which application paths are allowed to modify an entity.

Read the entity classes in `src/Catalog.Api/Persistence/CatalogDb.cs`. Exercise: predict the result if `second = new Tour()` happens before changing `second.Name`.

**Lesson 4 — Records, equality, and snapshots**

Your DTOs use records:

```csharp
public sealed record ConfirmSeats(Guid HoldId);
```

`public` exposes the type. `sealed` prevents inheritance. `record` declares a record class here, so it is still a reference type. The positional declaration generates a constructor and an init-only `HoldId` property, plus equality support.

```csharp
// Teaching example.
var id = Guid.NewGuid();
var first = new ConfirmSeats(id);
var second = new ConfirmSeats(id);
// first == second is true; ReferenceEquals(first, second) is false.
```

The records represent equivalent data while being separate objects. Ordinary entity classes use reference equality by default; database identity is established by keys such as `Id`. Record equality is not the project's durable deduplication mechanism.

`ReservationQueries.DetailAsync` uses a `with` expression to add compensation information to a returned `ReservationView`. This creates another record rather than mutating the original view. A record copy is shallow: array or object properties can still refer to the same nested mutable data. An init-only property does not make an entire object graph immutable. See [Microsoft's records documentation](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/types/records).

Entities are classes because EF tracks their identity and changes. Contracts are records because they carry snapshots across boundaries. A `record struct` would be a value type, but a Money struct was not introduced in this single-currency lab.

Read `src/Shared.Contracts/CatalogContracts.cs` and `ReservationContracts.cs`. Exercise: explain why two message records with equal values do not prove that a payment has never been processed.

**Lesson 5 — Nullability and validation**

`<Nullable>enable</Nullable>` activates compiler analysis of potentially missing references. `string?` means a string can be null; `string` expresses the intention that it is present. `Guid?` and `DateTimeOffset?` are nullable value types with an additional missing state.

Your request contract deliberately accepts potentially missing text:

```csharp
public sealed record CreateTour(string? Name, string? Description);
```

The handler validates it. Compiler annotations do not validate incoming JSON or replace business checks. In Catalog, required constructor-parameter handling also prevents missing mutation fields from silently becoming default false/zero values.

Learn these operators from the code: `?.` accesses a member only when the receiver exists; `??` supplies a fallback; `?? throw` rejects missing data; `!` suppresses a compiler warning but performs no runtime validation. `is not null` explicitly checks existence. `hold?.SessionId is Guid sessionId` checks and extracts a present identifier.

Null has domain meaning. Missing provider lookup data leads to uncertainty; it is not evidence of a decline. A nullable seat-hold session can describe a release tombstone created before any hold exists. The database constraint distinguishes valid tombstones from invalid Held records.

Read `SeatHold` in `CatalogDb.cs` and `GetChargeStatusAsync` in `PaymentProcessing.cs`. Exercise: explain why replacing every nullable result with a default object would erase useful information.

**Lesson 6 — Methods, constructors, inheritance, and interfaces**

A method names an operation with input parameters and a return type. `Task<QuoteView>` means the eventual result is a quote. `Task` without a type argument represents completion without a returned value.

```csharp
public sealed class CatalogQueries(CatalogDb db)
```

This primary constructor declares that constructing the queries object requires a `CatalogDb`. The parameter is available in instance members. Unlike positional record parameters, ordinary class primary-constructor parameters do not automatically become public properties.

`public CatalogDb CreateDbContext(string[] args) => ...` illustrates an expression-bodied method. `new(...)` is target-typed construction: the surrounding type determines which constructor is being called. Static methods belong to the type; instance methods operate on a specific object.

Interfaces describe replaceable boundaries. `ICatalogQuoteClient`, `IPaymentProvider`, `INotificationSender`, `IMessageTransport`, `IImageStorage`, and `IBookingStore` declare capabilities. `DurableFakeProvider : IPaymentProvider` supplies an implementation. Tests can substitute controlled implementations.

Inheritance appears where frameworks require it: `CatalogDb : DbContext`, workers derive from `BackgroundService`, and `CatalogComponent : ComponentBase`. `override` customizes a virtual framework operation. `abstract` means a class such as `CatalogComponent` cannot be instantiated directly.

Read `CatalogOperations.cs`, `PaymentProcessing.cs`, and `CatalogComponent.cs`. Exercise: explain why a concrete query class is enough while a payment-provider interface is useful.

**Lesson 7 — Generics and JSON contracts**

A generic type expresses a reusable shape while retaining the payload's type. Your `IntegrationEnvelope<T>` contains common message metadata plus `T Payload`. Thus `IntegrationEnvelope<HoldSeats>` and `IntegrationEnvelope<PaymentSucceeded>` share envelope structure but require different payloads.

Generic methods appear in `CatalogRules.AddIntentAsync<T>`, `ReservationIntents.Add<T>`, and `BusinessClient.SendAsync<T>`. `SendAsync<PaymentView>` tells the JSON client which result type to deserialize. This avoids manually casting every response.

The receiving transport initially parses `IntegrationEnvelope<JsonElement>`. `JsonElement` represents JSON whose business payload has not yet been converted to a specific C# contract. `MessageCodec.Payload<T>` supplies that conversion. A generic parameter is useful compile-time information; external messages still need runtime type/version/field validation.

JSON attributes such as `JsonIgnore` customize serialization. The envelope's optional `Tracestate` is omitted when null to preserve compatibility. `typeof(T).Name` supplies routing metadata; the allowed routes and explicit consumers still govern behavior. This is not a reflection dispatcher that searches for arbitrary handlers.

Generic constraints and covariance/contravariance are not needed by these boundaries. Adding them without a use case would obscure the code.

Read `CatalogContracts.cs`, `MessageCodec.cs`, and `BusinessClient.cs`. Exercise: explain which mistakes the compiler catches and which only message validation can catch.

**Lesson 8 — Lambdas, delegates, closures, and events**

A delegate is a callable value. A lambda is concise function syntax. In `.Where(t => t.Active)`, `t` is a parameter and `t.Active` is the result. DI registration and endpoint mapping also use lambdas.

`CatalogComponent.RunAsync` accepts `Func<CancellationToken, Task>`. Read that as: a function receiving a cancellation token and returning asynchronous completion. `ReservationSubmission.razor` passes `async ct => { ... }`, letting the shared component handle busy/error/disposal behavior around the action.

A closure lets a lambda use surrounding variables or instance state. The submission callback uses `Session`, `participants`, and `key`. This is convenient, but delayed or concurrent callbacks can observe changed captured state. The component stores an intention snapshot so a retry retains the accepted inputs.

Three uses of the word event must remain separate. Blazor's `@onclick` invokes a UI callback. A C# `event` is an in-process publisher/subscriber mechanism. An integration event such as `SeatsHeld` is serialized and delivered through durable transport. Your services do not communicate through C# events.

Read `CatalogComponent.cs` and `ReservationSubmission.razor`. Exercise: explain what captures the original booking intention and what would go wrong if a retry read newly edited quantity values.

**Lesson 9 — LINQ and database query execution**

LINQ expresses filtering, ordering, projection, and aggregation. `IQueryable<T>` lets a provider such as EF translate supported expressions into SQL. `IEnumerable<T>` describes enumeration; LINQ over materialized arrays/lists normally executes in memory.

Catalog builds a database query before fetching rows:

```csharp
var query = db.Tours.AsNoTracking().Where(t => admin || t.Active);
var total = await query.CountAsync(ct);
var rows = await query.OrderBy(t => t.Name).ThenBy(t => t.Id)
    .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
```

The first line builds a query expression. `CountAsync` executes a count query. `ToArrayAsync` executes the page query and materializes it. They are separate database operations; concurrent changes can affect their relationship. `ThenBy(Id)` gives ties a stable ordering.

`AsNoTracking` is useful for read-only data because EF does not need to track edits to those entities. It does not enforce authorization. Reservations explicitly adds owner filtering before materialization.

Avoid retrieving every reservation and then filtering Alice's rows in memory. The database should receive ownership filters and page limits. After materialization, `rows.Select(CatalogRules.View)` performs in-memory mapping into contracts.

Read `CatalogQueries.SearchAsync` and `ReservationQueries.ListAsync`. Exercise: identify which operators execute in PostgreSQL and which execute after data reaches C#.

**Lesson 10 — Exceptions and expected business failures**

An exception transfers control when the current operation cannot complete normally. A changed price, unavailable seats, or version conflict is an expected business outcome. A broken connection or programming error is an operational or unexpected failure.

Your implementation uses typed exceptions such as `CatalogProblem` and `ReservationProblem` for expected errors. Endpoint filters convert these into HTTP ProblemDetails with stable codes. This is a concrete implementation choice; expected failures can also be represented as typed results in other designs.

HTTP status codes communicate categories: 400 invalid input, 401 missing/invalid authentication, 403 insufficient permission, 404 missing or another user's detail, 409 business conflict, 503 required dependency unavailable, and 202 accepted durable work.

`catch (...) when (...)` is an exception filter. Reservation creation catches a PostgreSQL unique-key violation, rolls back the failed transaction, clears tracked state, and reads the winning request. It does not blindly retry every database failure.

`finally` runs cleanup even when the operation throws. The quote client releases its concurrency slot there. Safe centralized unexpected-error handling avoids exposing stack traces, credentials, or raw exception messages to callers.

Read `CatalogEndpoints.cs`, `ReservationHandlers.cs`, and `LabHosting.cs`. Exercise: compare a PriceChanged response with CatalogUnavailable and explain why their retry decisions differ.

**Lesson 11 — Async, tasks, and cancellation**

`async` allows `await` inside a method. A `Task` represents completion, failure, or cancellation. Awaiting incomplete asynchronous I/O suspends the method so the executing thread can perform other work. It does not inherently create a new thread or make CPU work parallel. See [Microsoft's async explanation](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/).

```csharp
// Repository pattern, shortened.
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

The second operation starts after the first completes. That ordering matters: returning acceptance before durable commit would mislead the user. Independent work can use `Task.WhenAll`; two simultaneous operations must not share one DbContext.

A `CancellationToken` requests cooperative cancellation. Your APIs propagate `ct` into EF, HTTP, transport, streams, and worker operations. Linked token sources combine caller cancellation with time budgets. The quote client distinguishes a caller abandoning a request from Catalog timing out.

Cancellation does not undo a transaction or fictional provider effect that already committed. A user may lose the response while the operation succeeds; durable identifiers let them query/retry safely.

`MessagePump` starts a visibility-renewal task and always cancels and awaits it in `finally`. That task has an owner and an observed outcome. Essential work must not be discarded as fire-and-forget. `.Result`, `.Wait()`, and `Task.Run` around network/database I/O are unnecessary here.

Read `Workers.cs` and `CatalogQuoteClient.cs`. Exercise: explain why cancelling a screen cannot be interpreted as refunding a payment.

**Lesson 12 — Dependency injection, lifetimes, factories, and Options**

Dependency injection supplies collaborators when constructing objects. `CatalogQueries(CatalogDb db)` makes its requirement explicit; the container creates it after registration.

| Lifetime | Meaning | Project example |
|---|---|---|
| Singleton | One instance per service container | TimeProvider.System, shared quote limiter |
| Scoped | One instance within a scope | Catalog handlers and API DbContext |
| Transient | New instance when resolved | Useful for short-lived stateless work; do not assume every registration uses it |

An API request normally gets its own scope. A Blazor Interactive Server scope normally lasts for a circuit, so scoped does not always mean short-lived.

Hosted workers are long-lived. `ConsumerWorker` receives `IServiceScopeFactory` and creates an async scope per delivery. The handler/context are resolved inside it and disposed afterward. Injecting a scoped context into a singleton and retaining it would create a captive dependency.

`IDbContextFactory<PaymentsDb>` lets the fake provider create a separate short context/transaction. `IdentityRegistration` registers a context factory; Identity stores are used in short HTTP/tool scopes rather than retained in components. `IHttpClientFactory` manages HTTP handler reuse while callers create request objects carrying their own authorization.

Options classes describe configuration. `IOptions<CatalogClientOptions>` exposes typed values. `Bind`, `Validate`, and `ValidateOnStart` reject invalid configuration before useful work begins. An interface or factory does not automatically make a dependency thread-safe.

Read `CatalogEndpoints.AddCatalog`, `IdentityRegistration.cs`, and `Workers.cs`. Exercise: draw worker singleton → new async scope → scoped handler → scoped DbContext.

**Lesson 13 — ASP.NET Core startup, middleware, and endpoints**

`Program.cs` uses top-level statements as the application's entry point. `WebApplication.CreateBuilder(args)` configures services and hosting. Registrations happen before `Build`; middleware/endpoints are configured on the built application; `Run` starts serving requests.

Methods such as `AddCatalog(this IServiceCollection services, ...)` and `MapCatalog(this WebApplication app)` are extension methods. The first `this` parameter allows calling them as if they belonged to the receiving object. They organize startup without creating another framework.

Middleware wraps request processing across the application. Your pipeline handles safe errors, adds safe logging/security headers, authenticates, and authorizes. Authentication determines identity; authorization determines allowed actions. Endpoint filters wrap a selected endpoint group, translating its business errors.

Minimal API lambdas bind parameters from routes, query strings, JSON bodies, dependency injection, and request cancellation. `{id:guid}` constrains a route segment. `RequireAuthorization("AdminApi")` attaches a backend policy; hiding an Admin button does not attach that policy.

For a reservation, follow Blazor client → Gateway `/api/...` → YARP → Reservations endpoint → creator handler → quote request → local database commit → 202 response. Workers continue the workflow after the HTTP request ends.

Read `Catalog.Api/Program.cs`, `CatalogEndpoints.cs`, and `ReservationEndpoints.cs`. Exercise: explain why an endpoint returning Task does not need to block on its result.

**Lesson 14 — EF Core, SQL, migrations, and transactions**

EF Core maps service-owned C# entities to database rows. A `DbContext` tracks a short unit of work. `DbSet<Tour>` is the entry point for querying/tracking Tour entities. `OnModelCreating` configures schema, tables, keys, indexes, relationships, and constraints.

`Add` tracks a new entity; `SaveChangesAsync` persists tracked changes. A single save is transactional by default for a relational provider, while an explicit transaction combines multiple saves/statements into one atomic unit.

```csharp
// Repository transaction pattern, shortened.
await using var tx = await db.Database.BeginTransactionAsync(ct);
// Track local business changes and outgoing intents.
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

Atomic means either the local transaction commits all its changes or none. It does not extend to another service, SQS, S3, or DynamoDB. A network request should not hold database locks open while waiting for the dependency.

Unique constraints prevent concurrent duplicate keys; check constraints enforce ranges and amount relationships even when competing application instances write. Runtime grants enforce schema ownership. A configured default schema/search_path alone is not a security boundary.

Migrations version the schema. Each service has its own migrations/history, applied by the separate provisioner before hosts start. Runtime hosts receive restricted DML credentials rather than migration privileges.

EF's context is not thread-safe and should remain a short unit of work. See [Microsoft's DbContext lifetime guidance](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/).

Read `CatalogDb.cs`, `ReservationsDb.cs`, and `tools/Lab.Provisioner/DatabaseBootstrap.cs`. Exercise: explain which guarantees would disappear if the Outbox insert happened after the business transaction committed.

**Lesson 15 — Concurrency, locks, versions, leases, and disposal**

Concurrency means operations overlap. Two users can request the same final seat. Checking availability in C# and later updating it leaves a race. Catalog uses transaction-held locks plus a conditional database update:

```sql
-- Teaching view of the statement in InventoryHandlers.HoldAsync.
UPDATE catalog."Sessions"
SET "AvailableSeats" = "AvailableSeats" - @participants,
    "Version" = "Version" + 1
WHERE "Id" = @sessionId AND "AvailableSeats" >= @participants;
```

One affected row means the condition was satisfied and the deduction occurred. Zero means the hold must be rejected. The implementation detaches the now-stale tracked session so EF does not write old values over the SQL result.

`Version` is configured as an EF concurrency token. Admin commands compare `ExpectedVersion`; conflicting edits fail rather than silently overwrite. Version tokens, database locks, and conditional statements provide different protections and are used together.

`lock` in `QuoteConcurrencyLimit` guards brief in-process breaker state; `SemaphoreSlim(10,10)` limits concurrent quote calls per instance. Neither protects inventory across service replicas. PostgreSQL locks and constraints do.

An Outbox lease is temporary durable permission to process a row. `SKIP LOCKED` lets another worker skip work already claimed. Lease-owner/expiry checks fence stale workers after recovery.

`using` disposes synchronous resources; `await using` awaits asynchronous disposal. Streams, HTTP requests/responses, scopes, contexts, and transactions must have clear owners. Garbage collection alone does not promptly release all external resources.

Read `InventoryHandlers.cs`, `OutboxDispatch.cs`, and `CatalogQuoteClient.cs`. Exercise: explain why confirmation does not deduct seats again; the hold already did.

**Lesson 16 — CQRS, service boundaries, and SOLID**

CQRS separates operations that change state from operations that read it. Your code uses `CatalogCommands`, `CatalogQueries`, `ReservationCreator`, and `ReservationQueries`. Explicit calls connect endpoints to handlers; MediatR is not required.

The initial command/query separation works with one service-owned database. The DynamoDB booking extension later adds a separate read model. A separate database is an optional architectural choice, not the definition of CQRS.

SOLID is useful when it helps explain actual responsibilities. Payment processing depends on `IPaymentProvider`, separating workflow policy from the replaceable provider effect. A query class can remain concrete because its current implementation does not need a replaceable abstraction. Small relevant interfaces are clearer than one interface for every class.

The Gateway calls business APIs; it does not join their tables. Contracts avoid exposing EF entities. Shared.Infrastructure accepts infrastructure primitives without becoming the owner of Catalog or Reservations rules.

Compared with a modular monolith, this lab teaches independent processes and asynchronous failure boundaries, but adds transport, deployment, tracing, and recovery complexity. More projects do not automatically produce a better product architecture.

Read the handlers and shared-project contents. Exercise: explain why directly calling EF in a query is appropriate here and why wrapping every DbSet in a generic repository would not automatically improve safety.

**Lesson 17 — Idempotency and stable identifiers**

Idempotency means repeating the same intended operation preserves its effect. A lost HTTP response should not create a second reservation or charge.

`ReservationCreator.CreateAsync` validates a key, computes a request hash, checks a stored result, obtains an idempotent quote, and commits the request key, reservation, Saga, history, and HoldSeats intent together. The key is scoped to user plus operation plus key. Changed content under the same key returns a conflict.

A pre-check improves the normal path but cannot settle concurrent requests. The database's composite key selects the winner. A losing transaction rolls back and reads the stored winning response.

| Identifier | Meaning |
|---|---|
| HTTP idempotency key | One user's operation intention |
| HoldId | One seat-hold operation |
| PaymentOperationId | One simulated charge |
| RefundOperationId | One refund linked to that charge |
| MessageId | One integration message identity |
| DeliveryId | One routed delivery identity |

Two messages with different MessageIds can describe the same payment operation. Inbox deduplication alone is therefore insufficient; the provider and business tables also enforce stable operation identities. Retrying compensation may generate a new message while reusing the same refund/hold IDs.

Read `ReservationHandlers.cs`, `SagaCommands.cs`, and `PaymentProcessing.cs`. Exercise: explain when a user should keep an intention key and when a deliberate new booking deserves a new key.

**Lesson 18 — Outbox, Inbox, queues, and DLQs**

The Outbox solves a database/queue boundary problem. Saving a reservation and publishing a message are different commits. Your service first commits business state plus the outgoing message intent locally. A worker claims that intent, sends it outside the transaction, then marks it published if it still owns the lease.

A crash after send but before marking published causes another send. Duplicates are allowed; the design does not claim exactly-once delivery.

For business consumers, the Inbox records `(ConsumerName, MessageId)` in the same local transaction as effects and outgoing intents. `InboxTransaction` also compares fingerprints: repeating an ID with contradictory content is poison, not a harmless duplicate. The consumer ACKs by deleting the SQS delivery only after the durable local work succeeds.

```text
Publisher: business state + Outbox → commit → send → mark published
Consumer: Inbox + effects + Outbox → commit → delete delivery
```

SQS is a queue, not automatic broadcast. Multiple destinations need multiple delivery records for the same message. Visibility hides a received delivery temporarily; it does not prove processing succeeded. `MessagePump` renews visibility during owned work.

Malformed/incompatible messages remain unacknowledged and eventually reach the DLQ. Inspection temporarily receives messages and affects visibility. Replay preserves identity, confirms sending before deletion, and may itself duplicate a delivery. Existing Inbox/business identity protections handle that boundary within their retention guarantees; the specified retention is fourteen days.

Read `LocalTransactions.cs`, `OutboxDispatch.cs`, `Workers.cs`, and `MessageCodec.cs`. Exercise: trace the two crash windows in `SendBeforeMarkCrashRecoversExpiredLeaseAndDuplicatesDoNotDeductTwice` and `ConsumerCommitBeforeAckCrashRedeliversWithoutRepeatingInventoryOrSagaTransition`.

**Lesson 19 — Sagas, deadlines, and compensation**

A Saga is a persistent coordinator for a workflow spanning services. Reservations owns the state machine; it sends commands and validates replies against current state, operation IDs, deadlines, and concurrency protections.

The successful states are:

```text
AwaitingAvailability → AwaitingPayment → AwaitingConfirmation → Confirmed
```

A decline starts release and stays Compensating until seats are confirmed released. A successful charge followed by rejected confirmation requires refund plus release. Cancellation starts from Confirmed before departure and remains CancellationPending until both complete.

Compensation is a new business operation correcting an already committed effect. It is not rolling back one global SQL transaction. Refund and seat-release replies may arrive in either order, so `RefundRequired/RefundCompleted` and `SeatsReleaseRequired/SeatsReleased` are persisted independently.

Deadlines are stored in PostgreSQL. Workers inspect due work after restart. An in-memory delay alone would lose the deadline decision on process exit. Late payment success after abandonment initiates refund; it cannot resurrect confirmation. A release arriving before a hold creates a tombstone that blocks the late hold.

ManualReview exposes unresolved work when bounded automation cannot establish/repair the outcome. Admin operations reconcile or retry the original work; they do not set the Saga directly to success.

Read `ReservationConsumer.cs`, `SagaRecovery.cs`, and `SagaCommands.cs`. Exercise: explain `ConfirmationFaultRefundAndReleaseCompleteInEitherOrder` and `AvailabilityDeadlineReleaseBeforeHoldAndLateHeldNeverStartPayment`.

**Lesson 20 — Durable simulated providers and HTTP resilience**

The provider is fictional, but its effect is persisted independently. `DurableFakeProvider` uses a factory-created context and stable operation ID. In TimeoutAfterCharge mode it commits a successful effect, then throws a timeout once. Payment processing records Unknown and later queries the same operation.

The crash boundary is deliberate: provider effect can commit before the Payments application result/Outbox commits. Reconciliation recovers the original effect instead of creating a new charge. The fake sender similarly persists a delivery receipt separately from Notification status. No actual bank or SMTP is involved.

The synchronous Catalog quote boundary uses `HttpClientFactory` and explicit resilience limits: three seconds per attempt, at most two retries, configurable total budget defaulting to ten seconds, ten simultaneous operations, and a circuit opening after five consecutive logical failures for thirty seconds. Those counters are per instance.

A logical failure counts the completed quote operation, not each retry attempt. Business conflicts and authentication responses are not indiscriminately retried. Caller cancellation is distinguished from dependency failure. TLS/protocol failures are not retried as though certificate validation could be bypassed.

Retry safely only when operation identity and dependency behavior support it. Bounded jitter/backoff reduces synchronized retries; it cannot make a non-idempotent payment safe by itself.

Read `PaymentProcessing.cs`, `NotificationProcessing.cs`, and `CatalogQuoteClient.cs`. Exercise: explain why an HTTP timeout proves that no answer arrived, while saying nothing definitive about whether a durable charge happened.

**Lesson 21 — Identity, JWTs, authorization, and TLS**

Identity stores users, password hashes, roles, and related authentication data in Gateway-owned storage. SSR login/logout forms establish/remove secure cookies with antiforgery protection. Blazor uses AuthenticationStateProvider to obtain the current verified identity.

Business API calls carry short-lived signed JWTs. The issuer signs with a private RSA key; APIs validate signature, expected algorithm, issuer, audience, lifetime, and required identity claims using public verification material. A signed but incomplete identity must still be rejected. The payload is not encrypted merely because the token is signed.

Roles answer whether an action class is permitted. Ownership checks answer whether the caller can access this specific resource. Reservations filters by the verified caller; another user's detail returns 404. Neither a client-supplied UserId nor a hidden UI button is sufficient authorization.

`BusinessClient` places Authorization on each `HttpRequestMessage`. It does not share a mutable DefaultRequestHeaders token among users. Reservations uses a distinct short-lived service identity permitted to create internal Catalog quotes; the Gateway blocks internal routes.

TLS validates peer certificates and hostnames. The local CA is explicitly generated/trusted; certificate validation is not disabled. Runtime connection strings require VerifyFull. Private keys, JWTs, passwords, and connection strings are excluded from versioned configuration/log output.

Identity plus the lab JWT issuer is not OAuth/OIDC. OIDC discovery, authorization-code flow, and PKCE belong to a possible later external-provider design. Logout/circuit revalidation and stateless JWT expiry have different revocation timing.

Read `IdentityRegistration.cs`, `JwtIssuer.cs`, `BusinessClient.cs`, and `LabHosting.cs`. Exercise: explain why each API still validates tokens even though the Gateway authenticates requests.

**Lesson 22 — Blazor components, circuits, and user intentions**

A Razor component combines markup with C# state and event callbacks. `@inject` supplies a service, `@bind` connects input and state, and `@onclick` invokes a callback. `[Parameter]` marks component input; `[EditorRequired]` aids tooling but is not a backend validation rule.

Interactive Server executes component logic on the server while the browser maintains an interactive connection. The circuit has a longer lifetime than an HTTP request. Components must not retain an EF context or HttpContext as if those were safe circuit-wide resources.

`CatalogComponent.RunAsync` centralizes busy/error behavior and passes the component lifetime token to its action. Disposal cancels outstanding I/O. `CircuitIsolationTests` verifies client scopes obtain fresh user identity and reject expired/signed-out state.

`ReservationSubmission` creates an intention key and snapshots Session, participants, and accepted price on first submission. A timeout retry preserves that snapshot/key. The deliberate NewIntention action resets them and requires price acceptance again. Disabling controls improves UX; server/database idempotency handles races and repeated requests.

Nonterminal detail views can poll while active; disposal and terminal states must stop unnecessary work. The UI shows temporary states because 202 is not final confirmation. A displayed projection does not authorize cancellation; the action uses authoritative Reservations data.

Read `ReservationSubmission.razor`, `CatalogComponent.cs`, and `ReservationDetail.razor`. Exercise: explain why scoped DbContext is safe for a short API scope but inappropriate to retain for the entire Blazor circuit.

**Lesson 23 — Image storage and DynamoDB projections**

The two extensions demonstrate different data-storage responsibilities.

For images, Catalog PostgreSQL owns metadata/lifecycle while S3-compatible storage owns bytes. `ValidatedImage.ReadAsync` spools a bounded stream, validates actual PNG/JPEG structure/decoding/dimensions, computes SHA256, and owns temporary-file disposal. A user-supplied MIME or filename is not trusted proof.

Catalog commits Pending intent before PUT, then performs a fenced Ready update. If storage succeeds but metadata commit fails, recovery HEADs the exact owned key and verifies its metadata. DeletePending and Deleted tombstones prevent a late upload completion from displaying deleted content. No SQL transaction spans the S3 call. Sixty-second signed read URLs are capabilities generated after authorization, not permanent stored URLs.

For My bookings, PostgreSQL remains authoritative and DynamoDB is an optional eventually consistent read model. `ReservationsDb.SaveChangesAsync` adds a self-contained BookingSnapshotChanged Outbox record in the same commit as a relevant reservation change.

`DynamoBookingStore.ApplyAsync` uses the atomic condition `attribute_not_exists(version) OR version < incomingVersion`. Version 3 followed by version 1 or another version 3 cannot regress the stored projection. Owner partition keys constrain lookups; newest-first GSI pages can lag. Encrypted/authenticated cursors bind owner, size, last key, and expiry.

This consumer deliberately has no PostgreSQL Inbox gate before DynamoDB PUT. Committing such a gate first could suppress retry after a failed external write. It ACKs after the conditional durable write or verified stale result. A crash before ACK duplicates delivery; version conditions handle it. Rebuild republishes bounded snapshots without wiping the table.

Read `ValidatedImage.cs`, `TourImages.cs`, `BookingStore.cs`, and `BookingProjection.cs`. Exercise: compare PostgreSQL transactions, S3 recovery intents, and DynamoDB conditions; explain why none establishes one transaction across all three systems.

**Lesson 24 — Tests, containers, telemetry, and practice**

Tests should challenge observable invariants. Pure tests cover isolated logic/contracts. PostgreSQL integration tests cover actual locks, constraints, transactions, and permissions. HTTP tests cover binding/auth/status behavior. Browser tests cover real interaction/circuits. Emulators exercise local storage/transport behavior but do not prove AWS IAM or production behavior.

The final-seat test creates two competing holds and asserts one winner plus zero available seats. It uses separate contexts through a helper. A replaceable TimeProvider advances deadlines deterministically. Crash-barrier callbacks such as afterSend/afterProvider interrupt precise boundaries without adding public crash endpoints.

| Requirement | Existing test to inspect |
|---|---|
| No overselling | CatalogTests.ConcurrentFinalSeatHasExactlyOneWinner |
| Release before hold is safe | CatalogTests.ReleaseBeforeHoldPersistsTombstoneAndPreventsLateDeduction |
| Send/mark crash tolerates duplicates | MessagingTests.SendBeforeMarkCrashRecoversExpiredLeaseAndDuplicatesDoNotDeductTwice |
| Consumer commit/ACK crash is safe | MessagingTests.ConsumerCommitBeforeAckCrashRedeliversWithoutRepeatingInventoryOrSagaTransition |
| Provider effect survives result loss | PaymentWorkflowTests.ProviderCommitCrashConcurrentRecoveryAndTimeoutLookupNeverChargeAgain |
| Compensation reply order is safe | SagaWorkflowTests.ConfirmationFaultRefundAndReleaseCompleteInEitherOrder |
| Quote concurrency is bounded | QuoteResilienceTests.TenConcurrentOperationsRejectEleventhAndCallerCancellationDoesNotRetry |
| Per-user clients stay isolated | CircuitIsolationTests.TwoClientScopesUseFreshActualIdentityOnEveryConcurrentRequestAndRejectExpiredOrSignedOutState |
| Storage/metadata recovery is safe | ImageExtensionTests.StorageSuccessThenMetadataFailureAndDeletionFailureRecoverFromOwnedIntents |
| Projection duplicates/order are safe | BookingExtensionTests.DynamoConditionalUpdatesPaginationAndForeignOwnerLookupsRemainSafe |

`Dockerfile` uses an SDK build stage and an ASP.NET runtime stage, with pinned images and non-root runtime user. Compose runs the hosts and dependencies locally. Stop/start preserves volumes/queues; deleting volumes is data destruction, not a normal pause.

Logs describe individual observations, metrics describe aggregate behavior, and traces relate operations across boundaries. `LabTelemetry` propagates safe identifiers and trace context while excluding secrets and high-cardinality business IDs from metric labels. Persisted business history remains the workflow source of truth.

Liveness checks the process without PostgreSQL; readiness checks only the owned database on demand. Optional local tracing is separate from the basic session. Terraform and guarded AWS scripts are reviewable artifacts; AWS deployment remains separately authorized LAB-009. This course gives no current cloud pricing claim or deployment evidence.

For an already prepared local environment, the documented commands include:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
```

These commands were not run for this document. A basic test invocation intentionally skips environment-dependent suites when their prerequisites are absent. Follow `README.md` and the relevant execution document for real integration checks; do not treat a skipped test as a pass. Browser verification gaps recorded in the repository still require actual runtime evidence.

**Final exercise — Explain one request without memorizing class names**

Choose a two-person booking. Explain its integer amount, immutable accepted input, verified owner, stable intention/operation IDs, authoritative quote, local transaction, durable Outbox, atomic seat hold, Inbox, provider crash boundary, reconciliation, final confirmation, and simulated notification. Then change the scenario to a late charge after hold abandonment and explain the refund/release path.

Your next lesson to study is Lesson 1, followed by Lessons 2–5. Predict each small code example before reading its explanation; then trace `ReservationCreator.CreateAsync` after the language foundations are comfortable.
