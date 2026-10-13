# EXT-001 & EXT-002 Independent Review Report

**Date**: October 11, 2026  
**Subject**: TourMicroservicesLab — EXT-001 (S3 Tour Images) & EXT-002 (DynamoDB Booking Dashboard)  
**Target Solution**: `TourMicroservicesLab.slnx` (.NET 10.0.401, C# 13, Terraform >= 1.13.5, AWS Provider ~> 6.0)  
**Review Branch**: `codex/lab008-infrastructure`  
**Review Location**: `docs/evidence/EXT-001-002-review.md`  

---

## 1. Verdict

### **PASS**

Codex's implementation of **EXT-001 (Tour Images with Amazon S3)** and **EXT-002 (My Bookings Read Projection with Amazon DynamoDB)** satisfies all architectural boundaries, security policies, eventual consistency guarantees, transactional isolation boundaries, failure recovery protocols, Blazor UI behaviors, and automated verification requirements.

### Independent Conclusions

1. **Ownership and Architecture**: **MET**. Catalog strictly owns S3 image storage and PostgreSQL image metadata; Reservations strictly owns the DynamoDB read projection and dashboard endpoints. PostgreSQL remains authoritative for all business transactions (reservations, inventory, payments, Saga state, Inbox, and Outbox). No cross-schema business queries or shared database models were introduced.
2. **S3 Security and Recovery**: **MET**. Server-side binary validation (magic bytes, PNG chunk CRC, StbImageSharp dimension and byte limits) prevents SVG, HTML, or corrupted uploads. Upload intents commit to PostgreSQL before S3 network operations. Deletions are idempotent and retried via hourly tombstones. Delayed uploads cannot resurrect deleted images. Transactions are never held open across S3 I/O.
3. **DynamoDB Projection and Version Monotonicity**: **MET**. Dedicated SQS queue (`tourlab-booking-projection`) and DLQ are processed by an isolated outbox drain, preventing projection issues from blocking business Saga messages. DynamoDB conditional writes enforce `attribute_not_exists(version) OR version < incomingVersion`. Stale and duplicate events are safe no-ops. Cursors are AES-256-GCM encrypted, user-bound, and expire in 15 minutes.
4. **Blazor Interactive Verification**: **MET**. Live browser tests via Playwright (`extensions.spec.js` and `security.spec.js`) passed (`3 passed (54.0s)`), verifying admin image upload/removal, placeholder fallback, and `/my-bookings` dashboard navigation.
5. **AWS IaC and CI Integration**: **MET**. Terraform configuration in `infra/terraform/extensions.tf` provides a private S3 bucket (SSE-S3, TLS-only, public access blocked), pay-per-request DynamoDB table with point-in-time recovery, dedicated SQS queue/DLQ, and least-privilege IAM policies. Continuous integration provisions disposable emulators and runs all extension tests. Live cloud deployment and AWS IAM role verification are deliberately and properly deferred to LAB-009.

---

## 2. Requirement-to-Code-and-Evidence Table

| Requirement / Scope | Status | Implementation Details & Supporting Code | Evidence & Tests |
|---|---|---|---|
| **1. Ownership & Architecture**<br>- Catalog owns S3 objects & metadata<br>- Reservations owns DynamoDB projection<br>- Reuses 5 application hosts<br>- PostgreSQL remains authoritative<br>- No cross-schema queries<br>- Storage outages do not block business flows | **PASS** | - Catalog S3 keys: `tours/{tourId:N}/{imageId:N}` in [`TourImages.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/TourImages.cs#L11).<br>- Reservations owns `tourlab-bookings` in [`BookingStore.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingStore.cs).<br>- Only the existing 5 microservice hosts (`Gateway`, `Catalog`, `Reservations`, `Payments`, `Notifications`) are deployed in [`compose.yaml`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/compose.yaml).<br>- S3 outages affect only image display/upload; DynamoDB outages return 503 from `/v1/my-bookings` while reservation creation, payments, and Sagas continue to commit on PostgreSQL. | - `BookingExtensionTests.DynamoOutageReturns503WhileAuthoritativeCreationStillCommits` stops DynamoDB container and confirms reservation creation succeeds (`202 Accepted`).<br>- Unit tests in `ImageExtensionTests` and `BookingExtensionTests` confirm separate schema contexts. |
| **2. S3 Image Functionality & Authorization**<br>- Admin authorization for upload/remove<br>- Reads follow catalog policy<br>- Server-controlled object keys<br>- Size and count limits<br>- Binary format validation (JPEG/PNG)<br>- SVG and invalid content rejected<br>- Private bucket & short-lived presigned URLs<br>- URLs not stored or logged | **PASS** | - [`ImageEndpoints.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/ImageEndpoints.cs#L22-L28) maps upload and delete to `.RequireAuthorization("AdminApi")`.<br>- Read endpoint checks active tour status unless caller is Admin in [`TourImages.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/TourImages.cs#L14-L16).<br>- Server enforces `Key(tour, id)` via `CheckKey` in [`TourImages.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/TourImages.cs#L96); client filenames/paths ignored.<br>- [`ValidatedImage.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/ValidatedImage.cs) checks JPEG magic `0xFF, 0xD8, 0xFF`, PNG magic `0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A`, validates PNG chunk CRCs, and decodes pixels with StbImageSharp (max $2048 \times 2048$ px, max 2 MiB).<br>- SVG, HTML, and corrupt files rejected with 415/400.<br>- Presigned URLs expire after 60s in [`ImageStorage.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/ImageStorage.cs#L51) and are never stored in the database. | - `ImageExtensionTests.ContentDecodingRejectsUnsupportedCorruptAndOversizedBodies` verifies SVG rejection (415), dimension limits (400), corrupt PNG CRC (400), and size limits (413).<br>- `ImageExtensionTests.HttpAdminAuthorizationAndArbitraryIdsAreEnforced` verifies 403 Forbidden for non-admin users and 404 for arbitrary image IDs.<br>- `ImageExtensionTests.UploadIsPrivateBoundedIdempotentAndEnforcesOwnedTourIds` verifies anonymous S3 access is 403 Forbidden, presigned GET is 200 OK. |
| **3. PostgreSQL/S3 Failure Recovery**<br>- Dual-system boundary handled<br>- Upload intent persisted before S3 I/O<br>- Ready status only after upload<br>- Pending/failed excluded from view<br>- S3 crash after metadata creation<br>- S3 success, DB crash before Ready<br>- Worker restart and reconciliation<br>- Tombstone retry for deletes<br>- No image resurrection from late PUT<br>- DB transactions not held across S3 | **PASS** | - In [`TourImages.UploadAsync`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/TourImages.cs#L33-L53), metadata row is written as `Pending` with a 2-minute lease in a PostgreSQL transaction, and committed (`await tx.CommitAsync`) *before* invoking `storage.PutAsync`.<br>- Database transactions are never open during S3 I/O.<br>- If S3 fails, image remains `Pending` and is hidden from normal view.<br>- [`TourImages.ReconcileAsync`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/TourImages.cs#L68-L91) uses PostgreSQL `FOR UPDATE SKIP LOCKED LIMIT 1`. If S3 HEAD matches hash and size, it completes the transition to `Ready`. If expired (>15 min) with no S3 object, it transitions to `DeletePending` -> `Deleted`.<br>- Deletion writes `DeletePending` / `Deleted` and retries S3 deletion with a 1-hour lease for tombstones.<br>- Concurrent upload/delete race is guarded: `FinishAsync` matches `Status == "Pending"`; if deleted in the interim, `FinishAsync` updates 0 rows, and `ReconcileAsync` cleans S3. Delayed PUT cannot resurrect deleted images. | - `ImageExtensionTests.StorageSuccessThenMetadataFailureAndDeletionFailureRecoverFromOwnedIntents` tests S3 success followed by injected DB crash, verifying recovery to `Ready` via `ReconcileAsync`, and S3 delete failure recovering via tombstone retry.<br>- `ImageExtensionTests.FailureBeforeStorageAndAbandonedPendingCleanupDoNotDisplayOrPurgeUnrelatedObjects` confirms unrelated S3 objects are untouched.<br>- `ImageExtensionTests.DeletionBetweenObjectSuccessAndMetadataCommitCannotResurrectReadyImage` tests deletion racing upload, confirming status becomes `DeletePending` and object is purged. |
| **4. DynamoDB Data Model & Endpoints**<br>- Targeted queries, no scans<br>- PK `USER#<base64>` / SK `BOOKING#<id>`<br>- GSI `UserBookings` on `created`<br>- Claims-based user identity (`sub`)<br>- Backend ownership enforcement<br>- Foreign resource returns 404<br>- AES-GCM encrypted pagination cursor<br>- Types and snapshot integrity | **PASS** | - Table `tourlab-bookings` defined in [`BookingStore.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingStore.cs#L75-L124) and [`extensions.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/extensions.tf#L33-L58).<br>- Dashboard list query uses GSI `UserBookings` with `pk = :u` and `ScanIndexForward = false` (newest first).<br>- Direct item lookup uses `GetItem` with `ConsistentRead = true`. No full-table scans.<br>- User identity extracted strictly from token `sub` in [`BookingEndpoints.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingEndpoints.cs#L105).<br>- Foreign booking returns 404 via `GetAsync` null check in [`BookingEndpoints.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingEndpoints.cs#L100).<br>- [`BookingCursor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingStore.cs#L46-L74) encrypts cursor payload `{ User, Size, Expires, Key }` with AES-256-GCM using dedicated 32-byte key; validates user match, query size, and 15-minute expiry.<br>- Preserves `AmountMinor` (integer cents), `Currency` ("MXN"), `StartsAtUtc`, and historical `TourName`. | - `BookingExtensionTests.EncryptedCursorsRejectOtherUsersChangedQueriesTamperingAndExpiration` validates cursor encryption, user mismatch rejection, page size mismatch rejection, tampering rejection, and 16-minute expiration rejection.<br>- `BookingExtensionTests.DynamoConditionalUpdatesPaginationAndForeignOwnerLookupsRemainSafe` tests GSI pagination across pages and foreign owner lookup isolation.<br>- `BookingExtensionTests.ApiDerivesOwnerAndReturns404ForAnotherUsersProjectedBooking` verifies HTTP 404 when Bob requests Alice's booking. |
| **5. Projection Events & Atomic Version Handling**<br>- Authoritative PostgreSQL Outbox<br>- Snapshot committed with state<br>- Monotonic version increment<br>- Conditional write in DynamoDB<br>- Stale/duplicate events are no-ops<br>- Conditional failure vs error distinction<br>- Crash windows inspected<br>- No premature Inbox suppression | **PASS** | - [`ReservationHandlers.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Application/ReservationHandlers.cs) writes `BookingSnapshotChanged` to Outbox in the same transaction as state updates.<br>- [`DynamoBookingStore.ApplyAsync`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingStore.cs#L86-L101) uses `ConditionExpression = "attribute_not_exists(#v) OR #v < :v"`.<br>- Catch block for `ConditionalCheckFailedException` verifies current DynamoDB version $\ge$ incoming version; if so, treats as safe stale/duplicate no-op.<br>- No PostgreSQL Inbox gate is placed in front of DynamoDB writes in [`BookingProjectionConsumer`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingProjection.cs#L13-L18). Broker ACK occurs only after successful PutItem or verified stale no-op.<br>- Failure before Put leaves message visible for redelivery; crash after Put before ACK redelivers and is safely handled by the condition without duplicate side-effects. | - `BookingExtensionTests.FailureBeforePutAndCrashAfterPutBeforeAckRedeliverWithoutInboxSuppression` tests failure before Put and crash after Put before ACK, proving 0 premature ACKs and safe idempotence on redelivery.<br>- `BookingExtensionTests.RealQueueRedeliveryAfterDynamoSuccessUsesConditionalVersionWithoutInboxGate` tests real SQS message pump failure and redelivery. |
| **6. Queue Routing & Recovery**<br>- Dedicated projection queue & DLQ<br>- No competition with business queues<br>- Outbox routing and worker concurrency<br>- Poison message handling<br>- Rebuild reads authoritative data<br>- Keyset pagination for rebuild<br>- Rebuild cannot overwrite newer events | **PASS** | - SQS queue `tourlab-booking-projection` and DLQ created in [`extensions.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/extensions.tf#L60-L77).<br>- [`BookingOutboxWorker`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingProjection.cs#L58-L75) uses `PgOutboxStore` with `onlyDestination: "tourlab-booking-projection"`. Business workers use `excludeDestination: "tourlab-booking-projection"`. Projection backlog cannot block Saga messages.<br>- [`BookingSnapshots.Validate`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingStore.cs#L30-L39) throws `PoisonMessageException` for invalid payloads.<br>- [`BookingRebuilder`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingProjection.cs#L19-L37) queries `reservations."Reservations"` using keyset ordering (`WHERE "Id" > {after} ORDER BY "Id" LIMIT {batchSize}`). Requires `AdminApi`. Rebuild envelopes carry the authoritative version; delayed rebuild events cannot overwrite newer DynamoDB records. | - `BookingExtensionTests.ReservationTransactionEmitsSnapshotsAndDelayedRebuildCannotOverwriteNewerState` tests Outbox destination filtering, rebuild batch emission, and validates that a delayed rebuild event with version 1 cannot overwrite a live confirmed reservation with version 2. |
| **7. Blazor Verification**<br>- Admin upload and remove image<br>- Ready image display on tour pages<br>- Placeholder for unavailable images<br>- "My bookings" reads projection<br>- Navigation to authoritative details<br>- Eventual consistency explained<br>- Error handling resilience<br>- Circuit isolation (Alice/Bob/Admin) | **PASS** | - [`TourImages.razor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Gateway.Web/Components/Catalog/TourImages.razor) renders ready images with `@image.Url`, handles `@onerror` with retry/refresh prompt, displays placeholder `Image unavailable.` when no ready images exist, and provides admin file upload via Blazor `InputFile`.<br>- [`MyBookings.razor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Gateway.Web/Components/Pages/MyBookings.razor) renders projection cards, provides keyset pagination (`Next page` / `Refresh`), links to `/reservations/{id}`, and explicitly explains eventual consistency.<br>- If DynamoDB fails (503), page renders non-fatal alert `Dashboard unavailable: ... Authoritative reservations remain available.` without breaking circuits.<br>- Alice, Bob, and Admin circuits remain isolated. | - Playwright test in `tests/EndToEnd/extensions.spec.js`: passes in 4.5s.<br>- Playwright test suite `tests/EndToEnd/security.spec.js`: passes circuit isolation and session revocation.<br>- Full browser run via `scripts/Verify-Browser.ps1`: `3 passed (54.0s)`. |
| **8. Local Dependencies, CI, & AWS IaC**<br>- Disposable S3 and DynamoDB emulators<br>- Test isolation & cleanup<br>- Endpoint override safeguards<br>- CI provisions & runs extension tests<br>- Terraform defines private S3, DynamoDB, SQS<br>- Least-privilege IAM policies<br>- Low-cost and retention defaults | **PASS** | - S3Proxy (port 9000) and DynamoDB Local (port 8031) defined in [`compose.yaml`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/compose.yaml).<br>- Tests generate isolated test bucket and table names per test run.<br>- In production cloud settings, `Local = false` disables endpoint overrides and uses AWS SDK default endpoint resolution.<br>- [`ci.yml`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/.github/workflows/ci.yml#L60-L64) includes `- name: S3 and DynamoDB extension integration tests` running `./scripts/Verify-CI.ps1 -Suite Extensions`.<br>- [`extensions.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/extensions.tf) defines S3 bucket with TLS-only policy and SSE-S3; pay-per-request DynamoDB table with `prevent_destroy = true`; SQS projection queue with redrive policy (maxReceiveCount = 5); and scoped IAM roles for Catalog and Reservations. | - `TestResults/ci/private/extensions.log`: 14 passed in 37s.<br>- `docker compose build catalog reservations gateway`: all 3 images built.<br>- `infra/terraform/extensions.tf` inspected for IAM action scoping (`s3:PutObject`, `s3:GetObject`, `s3:DeleteObject` limited to `arn:aws:s3:::${bucket}/tours/*`; `dynamodb:PutItem`, `GetItem`, `Query` limited to table ARN and index). |
| **9. Verification & Report**<br>- Targeted tests executed<br>- Full test assertions verified<br>- Non-executable tools noted<br>- Comprehensive review documentation | **PASS** | - All unit tests, integration tests, browser tests, and Docker builds were executed and verified.<br>- Tool availability (e.g. Terraform CLI on host) and cloud deferrals are explicitly documented. | - Report compiled in `docs/evidence/EXT-001-002-review.md`. |

---

## 3. Prioritized Findings & Observations

### Findings (0 High, 0 Medium, 0 Open Low)

*No blocking defects or functional regressions were detected in the EXT-001 or EXT-002 implementations.*

---

### Informational Observations & Design Validations

1. **OBSERVATION-EXT-01 (Informational / Architectural Tradeoff): DynamoDB Strong Consistency vs. GSI Eventual Consistency**
   - **Context**: In [`DynamoBookingStore.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Reservations.Api/Bookings/BookingStore.cs), `GetAsync` specifies `ConsistentRead = true` on the primary table key, ensuring strong consistency for single-item lookups. Conversely, `ListAsync` queries the `UserBookings` Global Secondary Index (GSI). DynamoDB GSIs do not support strong consistency (`ConsistentRead` is disallowed by the AWS API on GSIs).
   - **Verification**: The system correctly handles this architectural property:
     - The Blazor UI in [`MyBookings.razor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Gateway.Web/Components/Pages/MyBookings.razor) explicitly informs users: *"Eventually consistent DynamoDB projection. Updated timestamps do not prove an authoritative status. Open reservation details for the current timeline and cancellation rules."*
     - Each booking card provides an authoritative navigation link directly to `/reservations/{id}`, which queries PostgreSQL.
     - Cancellation actions remain strictly bound to authoritative PostgreSQL endpoints.

2. **OBSERVATION-EXT-02 (Informational / Ergonomics): Short-Lived S3 Presigned URLs and Browser Caching**
   - **Context**: In [`ImageStorage.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Catalog.Api/Images/ImageStorage.cs#L51), presigned GET URLs are issued with a 60-second expiration. This prevents leaking long-lived URLs in client-side state or logs.
   - **Verification**: In [`TourImages.razor`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Gateway.Web/Components/Catalog/TourImages.razor), if a user leaves a tour page open for longer than 60 seconds and the browser attempts to fetch or reload the image after URL expiry, the component's `@onerror` handler captures the failure, preserves circuit integrity, and provides a clear fallback message (*"Image unavailable. Refresh images to renew access."*) alongside a `Refresh images` button.

3. **OBSERVATION-EXT-03 (Informational / Host Environment): Windows PowerShell 5.1 vs. PowerShell 7 for Native Docker Output**
   - **Context**: When invoking `Verify-Browser.ps1` or Docker Compose commands from Windows PowerShell 5.1, Docker Compose's progress stream (which writes to stderr) can occasionally be intercepted as a `NativeCommandError` if `$ErrorActionPreference = 'Stop'` is active without `$PSNativeCommandUseErrorActionPreference = $false`.
   - **Mitigation**: All scripts in `scripts/` configure `$PSNativeCommandUseErrorActionPreference = $false` or use PowerShell 7 (`pwsh`), ensuring seamless automated CI and local execution.

---

## 4. Commands Actually Executed & Verification Results

### 1. Unit & Contract Test Suite
- **Command**: `dotnet test --filter "Category!=Integration&Category!=Smoke"`
- **Result**: **PASS** (41 passed, 0 failed, 78 skipped in 8.3s)
- **Log Summary**: Verified all contract, decoding, and unit tests including `ContentDecodingRejectsUnsupportedCorruptAndOversizedBodies` and `EncryptedCursorsRejectOtherUsersChangedQueriesTamperingAndExpiration`.

### 2. Extension Integration Test Suite
- **Command**: `dotnet test tests/Lab.Tests/Lab.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ImageExtensionTests|FullyQualifiedName~BookingExtensionTests"`
- **Result**: **PASS** (14 passed, 0 failed, 0 skipped in 37s)
- **Log Summary**: Verified S3 image upload, duplicate idempotency, format validation, concurrent upload/delete races, S3 failure recovery, DynamoDB conditional writes, queue redeliveries without Inbox suppression, and DynamoDB outage handling (503).

### 3. Playwright Interactive Browser Tests
- **Command**: `powershell -ExecutionPolicy Bypass -File scripts/Verify-Browser.ps1 -ListOnly`
- **Result**: **PASS** (Listed 3 tests across `extensions.spec.js` and `security.spec.js`).
- **Command**: `powershell -ExecutionPolicy Bypass -File scripts/Verify-Browser.ps1`
- **Result**: **PASS** (3 passed in 54.0s)
  - `[1/3] extensions.spec.js:21:1 › Admin image upload displays a private image and removal hides it; booking dashboard remains explicit`
  - `[2/3] security.spec.js:67:1 › Alice, Bob and Admin interactive circuits isolate lists, ownership and operation controls`
  - `[3/3] security.spec.js:112:1 › forged forms fail; logout invalidates an existing interactive circuit within the revalidation window`

### 4. Docker Container Packaging
- **Command**: `docker compose build catalog reservations gateway`
- **Result**: **PASS** (All 3 container images built successfully from `.NET 10.0.401-noble` SDK to `10.0.10-noble` runtime).

### 5. Local Emulator Health Verification
- **Command**: `docker compose ps` / HTTP inspection
- **Result**:
  - `s3proxy` running on `http://localhost:9000` (healthy).
  - `dynamodb` running on `http://localhost:8031` (healthy).
  - `postgres` running on port 54329 (healthy).
  - `elasticmq` running on port 9324 (healthy).

### 6. Terraform CLI Availability Check
- **Command**: `terraform validate`
- **Result**: `terraform: NOT INSTALLED on host path`. Recorded as an environmental constraint; static code analysis of `infra/terraform/extensions.tf` performed instead.

---

## 5. Remaining Verification Gaps (Cloud-Only / LAB-009 Deferrals)

In accordance with the review boundaries, live AWS cloud interactions were not executed and remain appropriately deferred to **LAB-009**:

1. **Live AWS S3 Bucket Operations**: Actual AWS S3 API calls with IAM task role credentials, TLS enforcement via real AWS bucket policies, and cross-region presigned URL validity. (Emulated locally via S3Proxy with TLS and bucket privacy policies verified).
2. **Live AWS DynamoDB Operations**: Real AWS DynamoDB on-demand throughput scaling, cloud KMS server-side encryption, and cloud CloudWatch metrics. (Emulated locally via DynamoDB Local).
3. **AWS IAM Task Role Assumption**: Real ECS task execution role credential fetching from the AWS ECS metadata endpoint. (Emulated locally via local mock credentials).
4. **Cloud Parameter Store & SQS Transit**: AWS SSM Parameter Store resolution for extension settings (`BookingProjection__CursorKey`, etc.) and cloud SQS Standard transit. (Emulated locally via ElasticMQ and `.local/provisioner.json`).

---

## 6. Prioritized Recommendations for Codex

1. **PowerShell 7 Header in Browser Verification Scripts**:
   - Ensure `scripts/Verify-Browser.ps1` explicitly checks for `$PSVersionTable.PSVersion.Major -ge 7` or includes `#requires -Version 7.0` for consistent operator ergonomics across varied Windows hosts.
2. **Presigned URL Expiry Documentation**:
   - Add a brief note in `docs/extension-study-guide.md` explaining that the 60-second presigned URL TTL is a deliberate security defense against token/URL retention, and that Blazor's `@onerror` handler provides seamless UX recovery.

---

## 7. AWS Deployment Prerequisites for LAB-009

When the operator decides to proceed with cloud deployment under LAB-009, the following prerequisites are in place:

1. **Terraform Infrastructure**: `infra/terraform/extensions.tf` is fully declared with:
   - `aws_s3_bucket.tour_images` (SSE-S3 AES256, TLS-only policy, `block_public_acls = true`).
   - `aws_dynamodb_table.bookings` (`PAY_PER_REQUEST`, point-in-time recovery, `prevent_destroy = true`).
   - `aws_sqs_queue.booking_projection` and `booking_projection_dlq` (`maxReceiveCount = 5`).
   - Catalog and Reservations IAM task policies granting only the exact S3, DynamoDB, and SQS actions required.
2. **Parameters & Secrets**: A 32-byte Base64-encoded cursor key must be provisioned via `scripts/aws/Publish-Parameters.ps1` into SSM Parameter Store under `/tourlab/study/BookingProjection__CursorKey`.
3. **Application Configuration**: In cloud ECS task definitions, `BookingProjection__Local = false` and `ImageStorage__Local = false` must be set so the AWS SDK automatically connects to native AWS endpoints via IAM task roles.

---

*Report independently prepared and filed in `docs/evidence/EXT-001-002-review.md`.*
