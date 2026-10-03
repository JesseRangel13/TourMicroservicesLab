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
