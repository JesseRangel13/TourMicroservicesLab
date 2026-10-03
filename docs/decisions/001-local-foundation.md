# ADR-001: LAB-001 local foundation boundaries

Accepted October 3, 2026.

Five independent hosts share one physical PostgreSQL database with five isolated schemas.
There is one restricted runtime login and one schema-owner migrator login per schema. A separate
local-only provisioner uses the lab's PostgreSQL administrator to create roles/schemas and then
uses identity_migrator for EF migrations and identity_runtime for seeding. Hosts never migrate
on startup and never receive provisioner credentials. No service shares an EF context/entity.

LAB-001 business schemas hold only infrastructure SchemaVersion=1. Service domain migrations,
Outbox/Inbox, and workers are introduced in their relevant tasks. Shared.Contracts deliberately
does not predefine every future message. Specifications 02/04 use EventId/MessageId differently;
that contract terminology must be explicitly resolved before LAB-003 transport implementation.
No money/inventory behavior is implemented based on that ambiguity.

Gateway co-hosts Identity, Blazor, and YARP as specified. Cookie establishment/sign-out happen in
short HTTP requests; no SignInManager or DbContext is retained by a component. Revalidation opens
and disposes a short scope, verifies the security stamp, and enforces an explicit per-session expiry.
Sign-out updates the stamp (invalidates other cookies/circuits for the same account). API JWTs are
separate stateless 15-minute credentials, validated for signature, algorithm, issuer, audience, and
lifetime at every API. This is a lab JWT issuer, not an OAuth/OIDC provider.

A locally generated CA signs host certificates, including PostgreSQL. Private files have a restricted
Windows ACL. Container processes use a public CA file for trust; no bypass callback is installed in
production clients. HTTP integration tests use a custom root trust chain that still validates the
hostname, certificate signatures, and expiry. Local CA revocation endpoints are absent. Data Protection
uses a separate encryption certificate; its encrypted key records persist in the identity schema.
Containers mount only the certificates/credentials they need. Runtime images run as `app`.

Durable JVM ElasticMQ 1.6.15 uses an H2 file on a named volume. All four business queues have a DLQ,
60-second visibility, 20-second long polling, and maxReceiveCount=5. Loopback HTTP for this simulated
local broker is explicit; AWS SQS will use its authenticated HTTPS endpoint in a later task.
This does not claim exactly-once delivery, an implemented Outbox, or payment/email simulation yet.

Local scripts target Windows/PowerShell 7 and Linux x86_64 Docker containers. Ports bind loopback.
AWS-specific networking, memory sizing, secret injection, and Neon permission checks remain pending.
No migration/seeding approach here implies authorization to provision cloud resources.
