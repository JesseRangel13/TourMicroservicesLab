# Catalog execution and manual checks

Run from the repository root in PowerShell 7. Existing `.local` configuration and database volumes
must be reused. `Start-Local` now performs a non-destructive config refresh before one-shot migrations:

```powershell
dotnet tool restore
./scripts/Start-Local.ps1
./scripts/Verify-Local.ps1
```

For an explicit upgrade of the running LAB-001 environment:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet run --project tools/Lab.Provisioner -- refresh
dotnet run --project tools/Lab.Provisioner -- bootstrap
docker compose build gateway catalog
docker compose up -d --wait gateway catalog
```

Check each command's exit status before continuing. Bootstrap uses catalog_migrator for EF DDL and
catalog_runtime for sample DML. Hosts never migrate on startup. No password or Identity record is
reset. Migration history is inaccessible to runtime roles. The provisioner's private settings never
enter a host's configuration. Sample sessions start 30 days after their first seed; repeated seeding
does not advance an existing departure or undo admin edits. Create new future sessions when needed.

Private config refresh creates a dedicated Reservations signing pair if both files are absent.
It refuses a half-present pair and preserves existing keys/passwords/certificates. Only its public
key is mounted into Catalog. The private key remains under the restricted `.local` ACL for local
tests and the future Reservations issuer; it is not mounted into Gateway. No token is printed.

For migration development (generate/review first; bootstrap performs the actual migration):

```powershell
dotnet ef migrations has-pending-model-changes --project src/Catalog.Api
dotnet ef migrations has-pending-model-changes --project src/Gateway.Web
# Only after intentionally changing the Catalog model:
# dotnet ef migrations add DescriptiveChange --project src/Catalog.Api --output-dir Persistence/Migrations
```

Read implementation decisions in `docs/adr/002-catalog-transactions.md`. Inventory handlers are not
HTTP endpoints. They receive a validated message context and run one transaction per operation.
Create a fresh scoped DbContext for each future consumed command/timer item. LAB-003 must integrate
Inbox insertion with the same transaction, rather than ACK first or wrap a separate committed handler
transaction. Refactor the transaction boundary when adding that adapter; existing invariant tests
must keep passing. Expiration currently requires calling the handler; no background timer runs yet.

Outbox rows contain an immutable version-1 envelope, stable MessageId/DeliveryId, original destination,
and effect key. `PublishedAtUtc` stays null. LAB-003 must add leased claiming, bounded sends outside
the transaction, ownership-checked publication updates and deduplicating consumers. Existing rows
are dispatchable intents, not evidence of delivery. Never purge tombstones on normal startup/pause.

| Local transaction | Persisted publication point |
|---|---|
| Create session; change price; change tour name/activation | TourSessionChanged with the new projection version |
| Successful conditional seat deduction + Hold insert | SeatsHeld |
| Persisted rejection or late hold against a tombstone | SeatsHoldRejected |
| Held → Confirmed before the deadline | SeatsConfirmed |
| Missing, released, expired or otherwise non-confirmable hold | SeatsConfirmationRejected |
| Held → Expired + one capacity restoration | SeatsHoldExpired |
| Release + restoration if needed; unknown release tombstone; already-expired release | SeatsReleased |

Duplicate operations reuse their persisted effect key and do not create another delivery row for the
same outcome. Capacity edits have no projection event because the defined informational projection
does not contain capacity. LAB-003 must ACK only after Inbox + effects + these intents commit.

## Manual browser checks (not automated evidence)

1. Trust the lab CA with `./scripts/Trust-LocalCa.ps1`, open https://localhost:8443 and sign in as Alice.
2. Open Tours, search by a substring, move through pages, open details, and inspect UTC departures,
   MXN prices and availability. Reservation submission must display "pending LAB-003".
3. Alice must be denied `/admin/catalog` and `/admin/catalog/{tourId}`.
4. Sign out using the protected form, sign in as Admin, and open Administration → Create a tour.
5. Create a tour, edit its name/description, deactivate it, and create a future UTC session. Admin
   search includes inactive tours; Tourist search/detail hides them. Reactivate the tour.
6. Change its price (integer MXN cents) and capacity. Confirm updated results and version numbers.
7. Open the same editor in two tabs. Save in one, then save stale data in the other. Expect
   VersionConflict, reload current values, and intentionally submit again. Do not automatically retry writes.
8. Try empty names, past departures, negative prices/capacity. Expect validation errors without a
   successful write. Stop Catalog temporarily and expect an unavailable/error message; restart it.
9. Repeat with two separate user browser profiles. Full interactive circuit isolation remains a
   LAB-007 check; server-rendered HTML checks do not establish it.

Tests leave uniquely named Evidence/HTTP evidence tours and operation rows for inspection. They do
not delete data or reset volumes. The normal script skips the separately enabled whole-lab restart
test. `Verify-Restart.ps1` temporarily interrupts the local lab and preserves all data.
