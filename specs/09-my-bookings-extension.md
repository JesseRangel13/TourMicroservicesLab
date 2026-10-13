# EXT-002 — DynamoDB My bookings projection

Reservations PostgreSQL owns booking facts, payments/inventory/Saga/Inbox/Outbox remain unchanged. DynamoDB is an optional read model, never a cancellation/payment authority. Reuse Reservations host with a separate projection queue/DLQ. Existing reservation APIs remain available.

## Snapshot and transactional publication

BookingSnapshotChanged v1 contains owner ID, reservation ID, immutable quoted tour-name snapshot, departure, participants, accepted integer minor units/currency, status, authoritative version and creation/update timestamps. Catalog quote persists its tour name; Reservations copies it at acceptance. Legacy records explicitly have unknown historical names. Catalog renames do not modify bookings.

Reservations saves a snapshot Outbox intent in the same PostgreSQL transaction as each relevant reservation change. Existing monotonic Reservation.Version is reused; every relevant mutation must increment it. One effect key per reservation/version. Queue `tourlab-booking-projection` and corresponding DLQ are distinct from the Saga command queue. Events are self-contained, no cross-schema reads or synchronous Catalog lookup.

## DynamoDB keys and consistency

Main PK `USER#<encoded-user>` / SK `BOOKING#<reservation-uuid>` supports owned consistent single-item reads. GSI UserBookings uses the same user partition and `createdAt#reservationId` ordering, newest first, for bounded pages1..20. No Scan. GSI is eventually consistent; detail may appear before a list entry and pages can change while events arrive. Cards label eventual consistency and link to authoritative `/reservations/{id}`.

Opaque authenticated/encrypted cursors bind the owner, query/page size, last evaluated key and15minute expiry. A dedicated private cursor key is supplied through configuration/SSM, never a client userId. Missing/foreign single items return404. Outage returns503 and the dashboard shows unavailable, without SQL fallback.

## Processing, recovery and rebuild

DynamoDB PutItem condition `attribute_not_exists(version) OR version < incomingVersion` atomically accepts newer snapshots. Equal/older snapshots are verified no-ops. Do not put a PostgreSQL Inbox gate before this external write. ACK only after application/conditional stale result. Crash after Put before ACK redelivers safely; failure before Put retains the delivery. Scope message validation and allowlists to this queue. SQS Standard allows duplicates/reordering.

Admin rebuild accepts a bounded keyset batch of owned PostgreSQL reservation snapshots and republishes the same version through normal Outbox/consumer path. No table wipe/Inbox reset; delayed rebuild cannot overwrite a newer version. Projection failures cannot prevent authoritative reservation transaction commits or worker compensation. Bounded retries and DLQ retain unresolved work.

## Acceptance and verification

BOOK-01: Alice/Bob isolation,404foreign detail, forged/wrong-user/expired cursors rejected; stable documented pagination and no Scan.
BOOK-02: initial and later states project with accepted immutable snapshots, no Catalog rename rewrite.
BOOK-03: duplicates/out-of-order and delayed rebuild remain monotonic.
BOOK-04: injected failure before Put and crash after Put before ACK remain retryable; no premature Inbox deduplication.
BOOK-05: DynamoDB outage shows explicit503 while authoritative creation/cancellation/payment paths remain usable.
BOOK-06: repeatable disposable table/index/queue bootstrap, least-privilege IaC and CI emulator tests, with AWS-specific consistency/IAM/capacity limitations documented.

LAB-009 remains pending. The original four-queue/no-Dynamo baseline is deliberately extended, not silently replaced. The current ALB versus this request's no-ALB topology conflict is recorded before networking changes and awaits user clarification.
