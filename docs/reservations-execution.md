# LAB-003 local execution

This documents the LAB-003 staging boundary. LAB-004 now enables Payments/Notifications consumers
and confirmation/release outcomes; use `docs/payments-execution.md` for current execution and demo.

Run commands from the repository root in PowerShell. Requirements: pinned .NET 10 SDK, Docker
Desktop Linux engine, trusted lab CA, existing private `.local` configuration. No AWS access is needed.

```powershell
./scripts/Trust-LocalCa.ps1
./scripts/Start-Local.ps1
./scripts/Verify-Local.ps1
./scripts/Verify-Messaging.ps1
./scripts/Verify-Restart.ps1
```

Startup refreshes existing runtime configuration without rotating credentials, starts PostgreSQL and
ElasticMQ, applies additive Identity/Catalog/Reservations migrations with isolated migrator roles,
initializes queues idempotently, and starts the five hosts. It does not reset schemas or volumes.
To run the one-shot steps separately while the database/broker are already running:

```powershell
dotnet run --project tools/Lab.Provisioner -- refresh
dotnet run --project tools/Lab.Provisioner -- bootstrap
dotnet run --project tools/Lab.Provisioner -- queues
docker compose up -d --build --wait
docker compose ps
```

`queues` uses the AWS SQS SDK against localhost only. It creates/reuses `tourlab-catalog`,
`tourlab-reservations`, `tourlab-payments`, `tourlab-notifications` and each corresponding `-dlq`.
Input queues use 60-second visibility, 20-second long polling and redrive after five receives. Queue
URLs and region live in private runtime configuration; `config/private.example.json` shows the shape.
Cloud mode uses HTTPS queue URLs, region and the SDK credential chain compatible with an ECS task
role. No cloud configuration was exercised or deployed. Do not supply AWS credentials for local mode.

Catalog/Reservations workers are enabled. Payments and Notifications do not register transport
workers: pending ProcessPayment and ReservationFailed messages are retained for LAB-004. SQS
retention is finite; a backlog is not indefinite archival storage. The durable producer Outbox remains
as evidence, and replay/reconciliation tooling belongs to later tasks. No queue purge is used.

Working flow: authenticated create → bounded service-authenticated quote → atomic reservation,
Saga, idempotency, history and HoldSeats intent → Catalog Inbox/inventory/result commit → Reservations
Inbox/state/history commit. Held seats produce AwaitingPayment and a stable ProcessPayment intent;
rejection produces Failed without payment work. Existing TourSessionChanged intents update a small
informational projection; quoting/inventory remain Catalog's authority.

Reservations handles SeatsHeld, SeatsHoldRejected and TourSessionChanged only. Other valid routed
result types, including hold-expiration/confirmation/release events, remain unacknowledged and can
reach DLQ; later tasks must provide the handlers/recovery policy. Catalog's existing hold-expiration
logic runs periodically, but full Saga deadlines and compensations are not implemented. A reservation
can remain AwaitingPayment after its hold expires. Do not interpret this state as a confirmed booking.

## API use

Use `/api/reservations/v1/reservations` through Gateway with an existing valid user Bearer JWT.
POST requires exactly one `Idempotency-Key` and a JSON body containing sessionId, participants (1–6),
expectedUnitAmountMinor and expectedCurrency (`MXN`). UserId is never supplied by the client.
202 includes the recorded identifiers and Location; it means durable acceptance, not confirmation.
Same user/key/content replays the original 202 even after the state changes; different content is 409.
Catalog failure is 503, stale accepted price/expired quote is 409, and neither creates reservation work.
GET list supports page/pageSize and status; Admin additionally filters userId and reads other owners.
Another tourist's detail/timeline is 404. Cancellation has no endpoint yet.

## Manual Blazor checks — not executed by automation for LAB-003

1. Open https://localhost:8443, sign in as Alice and open a future active tour session.
2. Select quantity, check accepted unit price/total and explicitly accept it. Submit once.
3. Inspect the reservation detail and timeline: AwaitingAvailability changes to AwaitingPayment,
   with an explicit LAB-004 payment-pending message. The active detail polls every three seconds.
4. Navigate away and verify polling stops. Terminal Failed reservations stop polling.
5. With a deliberately interrupted request, use Retry same intention: key, session, quantity and
   accepted amount stay frozen. Start a deliberate new intention generates a different key.
6. Open Own reservations, then sign in as Bob: Alice's reservation is absent and its URL returns 404.
   Admin can list/filter/read both users. Try competing for a session's final seat: one waits for
   payment and the rejected one shows Failed with a reason and no payment initiation.
7. Verify no functioning cancellation/payment-success buttons are displayed.

Submission intention is circuit/component state. A full refresh or lost circuit loses that state;
inspect the reservation list before deliberately starting another request. Do not claim durable
browser-side retry storage. Automated circuit isolation and browser E2E remain LAB-007.
The known browser sandbox failure was not retried; no new operator UI confirmation is claimed.

Controlled messaging/restart verification briefly interrupts this lab, pauses hosted workers and
restores them in `finally`. Run scripts sequentially and outside a demo. Crash callbacks are test-only
in-process seams, not HTTP endpoints. Tests create uniquely identified retained evidence rows/queues,
never purge queues or delete volumes. TRX reports are under `tests/Lab.Tests/TestResults/`.
