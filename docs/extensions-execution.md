# Run and study EXT-001 / EXT-002 locally

Requirements: .NET10.0.401, PowerShell7, Docker Linux; existing local lab CA trusted on the PC. Additional loopback ports9000 (S3Proxy) and8031 (DynamoDB Local). Do not run cloud commands for this exercise.

From the repository root:

```powershell
dotnet restore TourMicroservicesLab.slnx --locked-mode
dotnet build TourMicroservicesLab.slnx -c Release --no-restore
pwsh -File scripts/Start-Local.ps1
pwsh -File scripts/Verify-CI.ps1 -Suite Extensions
```

Start-Local preserves existing keys/passwords/data, refreshes private runtime files and a dedicated booking-cursor key, runs one-shot PostgreSQL migrations, starts the pinned storage emulators, initializes the owned bucket/table/index/queues and builds/starts the same five hosts. New additive migrations create Catalog.TourImages and add immutable tour-name snapshots to quotes/bookings. Legacy names are explicitly unavailable; they are not backfilled from today's Catalog. No data reset occurs. Bootstrap can be repeated; failures leave partial completed steps for inspection/retry, not deletion.

Manual equivalent, when images already built:

```powershell
dotnet run --project tools/Lab.Provisioner -c Release --no-build -- refresh
docker compose up -d --wait postgres
docker compose up -d elasticmq s3 dynamodb
dotnet run --project tools/Lab.Provisioner -c Release --no-build -- bootstrap
dotnet run --project tools/Lab.Provisioner -c Release --no-build -- queues
dotnet run --project tools/Lab.Provisioner -c Release --no-build -- extensions
docker compose up -d --wait gateway catalog reservations payments notifications
```

If emulator bootstrap races cold startup, retry that same command after a short wait; scripts retry at most12times. `extensions` is hard-coded to loopback and cannot create AWS resources. Local input queues/DLQs now number five; the fifth is solely for booking snapshots. Runtime SQS routing includes its URL but only Reservations has the cloud permission to use it.

## Image demo

1. Sign in Admin using private seed credentials. Open Administration → Catalog → Edit a tour. Select a small JPEG/PNG and upload. Maximum2MiB,2048×2048pixels, three active slots. Use smaller files for network/cost discipline. No SVG or arbitrary files.
2. Ready images appear on Tours and Tour Detail. Admin processing states remain visible; Pending/DeletePending are not displayed as images. Refresh images renews60second authorized GET capabilities. The local emulator is HTTP on loopback only; AWS S3 GET is HTTPS. Browser URLs are capabilities; do not paste them into logs/evidence.
3. Retry the same selected file after an uncertain upload with the same intention ID. Selecting a new file generates a new ID. Reusing an existing ID with changed bytes is409. A busy/leased intention can return a temporary409 until reconciliation/retry.
4. Remove an image. Response accepts durable deletion, so refresh after reconciliation. Worker cycles every15seconds, leases at most ten records per cycle, and waits for an active upload lease to expire. A lost metadata commit can recover Ready through HEAD; an absent Pending object older15minutes becomes exact-key deletion work. Deleted IDs are retained as tombstones and cannot be re-uploaded.

The image worker uses scoped contexts and separate object calls outside transactions. No bucket scan/purge exists. S3Proxy's filesystem implementation does not verify AWS bucket encryption/public-access/IAM; those are IaC/cloud checks. This MVP decodes images but is not a malware-scanning pipeline, does not strip EXIF or create thumbnails. CSS width does not reduce downloaded bytes.

## Booking demo and recovery

Deleted image tombstones retry exact-key deletion hourly during active sessions to repair a late/uncertain remote PUT. This never makes deleted metadata visible and never scans/purges a bucket. Work pauses with the hosts and resumes next session. Include these requests in costs for long-lived test datasets.

1. Sign in Alice and create a booking through Tours as before. Open **My bookings**. Cards clearly identify DynamoDB eventual consistency and show the accepted name, departure, participants, integer-cent total, currency, status, version and update time. Navigate to authoritative details for timeline/cancellation.
2. Cancel through authoritative detail when allowed; wait/refresh the dashboard. A projected status never authorizes cancellation. A later Catalog rename does not rewrite accepted names.
3. Try Bob's session: his list derives his token subject and foreign card detail returns404. Pagination is newest-first via UserBookings; authenticated encrypted cursors expire after15minutes and are bound to owner/page size. Concurrent index updates can affect pages; refresh starts a new query. No Scan or PostgreSQL fallback exists.
4. For local outage observation, `docker compose stop dynamodb`; My bookings shows unavailable, while authoritative Reservations remains available. Restore with `docker compose up -d dynamodb`, then rerun `extensions` if needed. No table wipe. Automated checks restore the emulator in finally.
5. Admin **Rebuild next batch** publishes20snapshots from a PostgreSQL UUID keyset. Continue until finished; **Start a new rebuild pass** covers rows inserted before a previous cursor. Each snapshot keeps its authoritative version, so delayed rebuilds cannot overwrite newer events. Existing stale cursors do not force a table/Inbox reset.

Projection poison deliveries remain in its dedicated DLQ after five receives. Inspect/replay only the projection queue/DLQ with the scoped operator tooling and original envelope/IDs; never feed them to the Saga command queue. Rebuild is the supported admin recovery for valid authoritative data. Fix a malformed contract/handler before explicit scoped replay; repeated publication of the same bad payload is not recovery. No arbitrary-message browser button is added.

## Verification and CI

`Verify-CI -Suite Extensions` pauses the existing business workers, runs unit and emulator integration cases with preserved state, then restores workers. It does not delete test objects/tables; unique local bucket/table/tour IDs isolate cases. `-Suite Live`, Controlled and Restart remain the business regression checks. CI provisions disposable PostgreSQL, ElasticMQ, S3Proxy and DynamoDB Local in a run-specific Compose project, runs Extensions, builds five images and publishes only existing safe reports. Raw logs, TRX, private configuration/cursor keys/certificates, state and signed URLs are never uploaded. GitHub execution must be observed separately.

Infrastructure checks use Terraform1.13.5 and the existing locked AWS provider:

```powershell
terraform -chdir=infra/terraform init -backend=false
terraform -chdir=infra/terraform fmt -check -recursive
terraform -chdir=infra/terraform validate
terraform -chdir=infra/terraform test '-var-file=example.tfvars.json'
pwsh -File scripts/aws/Test-Guards.ps1
```

Mock plans generate fictional attributes at plan time; no AWS credentials or protected-resource teardown is involved. This is not a real provisioning plan.

## LAB-009 gates added by these extensions

- Independently review EXT specifications, migrations, runtime size/cold starts, decoder limits and retention. Existing LAB-008 review does not approve this diff; LAB-009 remains incomplete.
- Default `enable_alb_study_profile=false` follows the requested no-ALB/no-NAT profile. Existing ALB teaching artifacts are opt-in with separate HTTPS/circuit/cost review; their HTTP listener is not a verified secure login front door.
- Review real S3 public-access block, ownership controls, TLS-only policy, default SSE-S3 and exact `tours/*` IAM. No runtime ListBucket/PutBucket or arbitrary-key access. Confirm presigned URLs expire and are not logged.
- Review DynamoDB table/GSI schema and conditional-write/GSI consistency behavior, task-role Put/Get/Query permissions with no Scan/Create/DeleteTable, and the dedicated queue/DLQ policy/visibility. Test throttling, outages, reordering and replay in actual AWS only after separate authorization.
- Generate/preserve `.local/aws/booking-cursor.key` separately from Gateway signing/protection material and publish only Reservations' `BookingProjection__CursorKey` Standard SecureString. Existing private cloud directories need an explicit key addition; never rerun fresh key generation over them. A dedicated32byte cryptographic random key encoded as Base64 fits the parameter limit. Update private tfvars/session references from examples and restart tasks to refresh secrets. Key rotation invalidates outstanding pagination cursors.
- Run one-shot owned schema migrations with direct Neon credentials outside tasks; verify runtime access to new owned tables and cross-schema denials. Review/back up data before changes.
- Supply existing bucket/table/queue outputs through Terraform runtime configuration; no emulator credentials/endpoints in cloud. Publish the five versioned images only in the separately authorized deployment task.
- Review incremental request/storage/transfer costs in docs/costs.md and test a short session. Stop preserves images, projection, PostgreSQL and queues. S3 bucket refuses nonempty destruction; DynamoDB protection prevents Terraform destruction. A whole-stack destroy plan is intentionally blocked until an explicit, separately reviewed data-deletion/retention procedure changes those protections. Do not disable guards or delete live data to clean up tests.
- Record actual UI upload/read/remove, booking projection transitions/outage/rebuild, owned IAM denial, pause/start persistence, spend and zero tasks in LAB-009; no local emulator test proves these cloud behaviors.
