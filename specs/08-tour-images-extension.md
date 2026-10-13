# EXT-001 — Catalog tour images

This extension explicitly permits small images, S3 and an S3-compatible local emulator beyond the original no-image study baseline. PostgreSQL Catalog remains metadata authority. No additional host or cloud deployment. Existing business workflows are unchanged.

## Contracts and limits

Admin PUT `/v1/admin/tours/{tourId}/images/{imageId}` sends raw image bytes, with an intention UUID preserved on retry. Only JPEG/PNG verified by signature, dimensions and full decoding; SVG and arbitrary files rejected. Maximum2MiB,2048x2048pixels, three nondeleted image slots per tour, two concurrent validation/upload operations per process. Filenames, declared MIME, bucket and keys never authorize storage. Backend generates `tours/{tourId:N}/{imageId:N}` in its configured private bucket.

Authenticated GET `/v1/tours/{tourId}/images` returns ready image IDs/content metadata and60second presigned GET capabilities after the existing active-tour/Admin visibility check. Admin GET lists processing states; Admin DELETE by IDs durably requests removal, idempotently. No arbitrary-key endpoint. Signed URLs are never persisted/logged and may remain usable until expiry/deletion. The browser displays placeholders on missing/expired images and offers refresh.

## Persistence and failure boundaries

Catalog TourImages: UUIDs, server key, validated MIME, size, SHA256, Pending/Ready/DeletePending/Deleted status, creation time, version, work lease. Lock the owned Tour row for slot allocation; unique image ID and immutable hash protect same-intention retries. Validate/spool a bounded stream first, then commit Pending intent before S3. Never hold SQL transactions during object calls. Ready requires successful storage and a fenced metadata update.

Recovery leases at most ten due records per cycle. Expired Pending work HEADs its exact owned key: matching metadata recovers Ready after storage-success/DB-failure; absent abandoned intents older15minutes become deletion work. DeletePending waits for an upload lease to expire, deletes the exact key idempotently, then retains a Deleted tombstone. Storage failure leaves durable work retryable. Deletion wins a race with upload; a conditional Ready update cannot resurrect it. No bucket scans/purges, broad cleanup or data destruction. Network operations have bounded timeouts/cancellation; workers create per-operation scopes.

## Acceptance and verification

Deleted tombstones remain eligible for bounded hourly exact-key deletion during active sessions, repairing an uncertain late PUT after an earlier deletion. No bucket scan is needed. When tasks are paused, repair resumes next session; metadata remains hidden regardless of object timing.

IMG-01: missing identity/nonAdmin cannot upload/delete; inactive-tour policy and arbitrary IDs/keys cannot bypass access.
IMG-02: JPEG/PNG upload displays; unsupported, corrupt, excessive dimensions/size and fourth slot rejected; cancellation propagated.
IMG-03: same ID/body retry is idempotent; changed bytes conflict; storage failure retains Pending.
IMG-04: storage success followed by failed metadata commit is recovered through matching HEAD metadata, without a second key.
IMG-05: repeated deletion/storage failure/retry and upload/delete race never display deleted work or broadly remove objects.
IMG-06: local emulator initialization is repeatable; private S3 IAM/public-access/TLS protections are IaC artifacts; actual AWS remains unverified.

Tests use real PostgreSQL and local S3-compatible storage plus narrowly injected failure boundaries. Record decoder/emulator limits. Future direct browser presigned PUT is a separate alternative requiring content verification, CORS and cleanup; it is not implemented alongside this bounded InputFile path.
