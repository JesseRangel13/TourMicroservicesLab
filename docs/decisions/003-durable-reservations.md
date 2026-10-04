# LAB-003: durable creation and staged consumers

LAB-002's reviewed handlers own a transaction when called directly. The message adapter must own
the transaction that also inserts Inbox. A small transaction-owner helper lets these handlers join
an existing EF transaction without committing it. Exceptions roll back Inbox, effects and Outbox.
This is an integration prerequisite, not a reviewed LAB-002 inventory defect.

MessageId identifies a business message; DeliveryId identifies a destination delivery. The existing
Catalog rows are migrated in place with nullable lease ownership/deadline and attempt counters.
Both publishers use the same SQL lease algorithm against their own schema. EF entities remain local.
An explicit route table creates one delivery per destination with one business MessageId. There are
no in-memory events. Claims commit before SendMessage; marking sent requires the current lease owner.

Reservations persists its original 202 body and Location. A deterministic SHA-256-derived UUID from
length-delimited user/operation/key identifies the Catalog quote intention. A separately stored hash
of typed request content detects different payloads. Quote HTTP happens outside a transaction. A
unique user/operation/key wins concurrent commits; losers roll back before reading that result.

LAB-003 transitions AwaitingAvailability to AwaitingPayment and publishes ProcessPayment with the
preassigned PaymentOperationId. Inventory rejection transitions to Failed without payment and
persists ReservationFailed for future Notifications. Payments and Notifications have NO consumers
enabled. Their pending commands/events stay in durable queues for LAB-004; no success is fabricated.

Catalog expiration runs locally and publishes SeatsHoldExpired. Payment/expiration compensation,
availability timeouts and cancellation remain LAB-005. Unsupported outcomes are NOT acknowledged;
after the configured receive limit they remain in the Reservations DLQ for future controlled replay.
Old LAB-002 test intents with nonexistent Sagas are similarly quarantined rather than discarded.
The live UI warns that AwaitingPayment is a staging boundary, not confirmed seats/payment success.

TourSessionChanged needs a consumer to preserve existing Catalog intents. A minimal informational
Reservations projection accepts increasing PriceVersion; it never authorizes inventory or price.
Projection UI/diagnostics and broader fault controls remain LAB-006. Deduplication rows/tombstones
have no automatic deletion and must be retained at least 14 days. AWS is configuration-only here.
