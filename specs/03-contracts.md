# 03 — HTTP and Message Contracts

## Conventions
/v1, camelCase JSON, UUIDs, ISO-8601 UTC timestamps, and integer minor units for money. Pagination: page>=1, pageSize 1..50. ProblemDetails includes a stable code and traceId, without stack traces or secrets. Use 400 for invalid input, 401 for missing/invalid identity, 403 for insufficient permissions, 404 for missing or another user's resource, 409 for business conflicts, 503 for an unavailable required dependency, and 202 for accepted durable work.
All protected APIs validate JWTs; login and liveness endpoints are explicit exceptions. A role restriction in the UI alone is not authorization.

## Human-facing surface: every operation requires a Blazor screen or control
Gateway Identity: POST /auth/login (form + antiforgery), POST /auth/logout, POST /auth/token (verified credentials for API tools; rate limited), GET /auth/me. Cookies for Blazor; JWTs for APIs. No endpoint that issues a token merely by selecting a name or role.
Catalog, through /api/catalog:
- GET /v1/tours?search=&page=&pageSize= — list.
- GET /v1/tours/{tourId} — detail.
- GET /v1/tours/{tourId}/sessions — dates/prices/capacity.
- POST /v1/admin/tours — name, description.
- PUT /v1/admin/tours/{tourId} — name, description, active, expectedVersion.
- POST /v1/admin/tours/{tourId}/sessions — startsAtUtc, unitAmountMinor, capacity.
- PUT /v1/admin/sessions/{sessionId}/price — unitAmountMinor, expectedVersion; increment PriceVersion and publish TourSessionChanged.
- PUT /v1/admin/sessions/{sessionId}/capacity — capacity, expectedVersion; never below occupied/held seats.
Reservations, through /api/reservations:
- POST /v1/reservations, mandatory Idempotency-Key header; body: sessionId, participants, expectedUnitAmountMinor, expectedCurrency. Obtain UserId from the token.
- GET /v1/reservations — own reservations only; Admin may filter by userId/status.
- GET /v1/reservations/{id} — snapshot, agreed price, status, and reason.
- POST /v1/reservations/{id}/cancel, Idempotency-Key; start compensation, not a destructive DELETE.
- GET /v1/reservations/{id}/timeline — business transitions without secret messages.
- GET /v1/admin/sagas?state= — process state and deadlines.
- POST /v1/admin/sagas/{id}/retry-compensation — same operation, not a new refund ID.
- GET /v1/projections/tour-sessions — informational copy with updatedAt.
Payments, through /api/payments:
- GET /v1/payments?reservationId= — own records or Admin.
- GET /v1/payments/{id} — outcome and associated refund.
- POST /v1/admin/payments/{id}/reconcile — query the provider with the original ID; do not initiate another charge.
Notifications, through /api/notifications:
- GET /v1/notifications?reservationId=&page=&pageSize= — own records or Admin.
- GET /v1/notifications/{id} — fake content and status.
Administration on each business service:
- GET /v1/admin/diagnostics — counters, redacted Outbox/Inbox data, and worker status.
- PUT /v1/admin/faults — permitted mode and occurrence limit; only when LabFeaturesEnabled.
- POST /v1/admin/faults/reset — restore normal behavior.
- GET /v1/admin/dead-letters — redacted list from its DLQ, pageSize<=10.
- POST /v1/admin/dead-letters/{deliveryId}/replay — send to its original queue, preserving MessageId. Replay only after correcting the error and write an audit log. Do not expose receipt handles to the browser. SQS inspection has its own visibility/lease semantics. Replay confirms successful sending before removing the DLQ message. Document that listing a DLQ is not a passive SQL query.

## Internal quote contract
POST Catalog /v1/internal/quotes, accessible only to the Reservations service identity with quote:create permission. Include a stable requestId per (userId, reservation-create idempotencyKey), sessionId, participants, userId, expectedUnitAmountMinor, and expectedCurrency. Return quoteId, unitAmountMinor, totalAmountMinor, currency, expiresAtUtc, and session information.
Repeating requestId with identical input returns the same quote while it remains usable; different input returns 409. A price different from the accepted price returns 409 PriceChanged, without a charge. An expired quote returns 409; the client needs a new intention and must confirm the price. YARP must not publish this route; Blazor cannot invoke it directly.

## Durable response example
POST reservations returns 202, Location=/api/reservations/v1/reservations/{id}, and:
{ "reservationId":"UUID", "status":"AwaitingAvailability", "sagaId":"UUID" }
A retry with the same key preserves the resource and original response. If the original request is still processing, return a documented and tested retryable temporary conflict or a result lookup. Do not fabricate success before commit.
If Catalog fails during quoting, return 503 and create neither a Saga nor a payment; reuse the key on a later retry. Deferred PendingValidation is a documented extension, not hidden MVP behavior.

## Envelope
{
  "messageId":"UUID", "deliveryId":"UUID", "type":"ProcessPayment", "version":1,
  "occurredAtUtc":"2026-10-02T22:00:00Z", "sagaId":"UUID",
  "correlationId":"UUID", "causationId":"UUID", "traceparent":"...", "payload":{}
}
No JWTs, private keys, or card details in payloads. Include required identities and amounts from authorized sources. Validate type/version/fields; never silently replace missing required UUIDs or values.

## Routes and minimum payloads
| Type | Publisher → consumer | Fields in addition to SagaId |
|---|---|---|
| HoldSeats | Reservations → Catalog | holdId, quoteId, reservationId, userId |
| SeatsHeld | Catalog → Reservations | holdId, expiresAtUtc |
| SeatsHoldRejected | Catalog → Reservations | holdId, reason |
| ProcessPayment | Reservations → Payments | paymentOperationId, reservationId, userId, amountMinor, currency |
| PaymentSucceeded | Payments → Reservations | paymentOperationId, providerReference |
| PaymentDeclined | Payments → Reservations | paymentOperationId, reason |
| PaymentOutcomeUnknown | Payments → Reservations | paymentOperationId, nextReconcileAtUtc |
| ReconcilePayment | Reservations → Payments | paymentOperationId |
| ConfirmSeats | Reservations → Catalog | holdId |
| SeatsConfirmed | Catalog → Reservations | holdId |
| SeatsConfirmationRejected | Catalog → Reservations | holdId, reason |
| SeatsHoldExpired | Catalog → Reservations | holdId |
| ReleaseSeats | Reservations → Catalog | holdId, reason |
| SeatsReleased | Catalog → Reservations | holdId |
| RefundPayment | Reservations → Payments | refundOperationId, paymentOperationId, reservationId, userId, amountMinor, currency |
| PaymentRefunded | Payments → Reservations | refundOperationId, paymentOperationId |
| RefundNeedsReview | Payments → Reservations | refundOperationId, reason |
| ReservationConfirmed | Reservations → Notifications | reservationId, userId, summary |
| ReservationCancelled | Reservations → Notifications | reservationId, userId, summary |
| ReservationFailed | Reservations → Notifications | reservationId, userId, reason |
| TourSessionChanged | Catalog → Reservations | sessionId, tourName, active, priceVersion, unitAmountMinor, currency |

Unknown is not a terminal event; a later outcome may resolve it. Projection consumers ignore older versions even when they arrive later. Commands carry a fixed OperationId/HoldId; new deliveries do not create new IDs. Shared.Contracts supports serialization tests independent of EF.
