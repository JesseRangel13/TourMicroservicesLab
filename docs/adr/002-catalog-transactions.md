# Catalog transaction decisions (LAB-002)

The specifications use EventId in architecture prose and MessageId in the concrete envelope.
MessageId is the business event identity; DeliveryId identifies a destination delivery. No third ID
is introduced. Catalog persists envelope JSON and a unique destination/effect key in its own Outbox,
in the transaction that changes state. Nothing dispatches these rows in LAB-002.
TourSessionChanged is not associated with a reservation Saga, so its envelope SagaId is null;
inventory outcomes require the original nonempty SagaId. Correlation/Causation IDs remain nonempty.
This explicitly resolves the envelope example's SagaId assumption for non-Saga catalog changes.

Inventory handlers acquire a PostgreSQL transaction advisory lock for HoldId, then a Session row
lock. Holding also locks the parent Tour before the Session to serialize deactivation. Admin changes
lock Tour then Session; they never lock a Hold. Quotes lock RequestId, then Tour and Session.
No transaction acquires those locks in reverse order. Conditional SQL performs the seat deduction.
Confirmation, expiration, release and capacity edits share Session row locks across processes.

Unknown releases persist a Released tombstone with nullable session/quote metadata and zero seats.
Rejected holds also persist their outcome so a repeated HoldId cannot later become successful.
Each quote belongs to one hold intention (unique QuoteId in Holds). Contradictory operation identities
raise a conflict for the future transport adapter to quarantine, rather than overwrite state.
Tombstones and quotes are never purged by this implementation; LAB-003 must preserve the specified
minimum 14-day deduplication retention. Consumed/terminal quotes cannot create another hold.

PriceVersion orders TourSessionChanged projections. Name/activation changes increment it for each
affected session too, otherwise consumers could discard changed projection content with the same
version. This does not invalidate a quote's agreed amount. Quote responses include current session
information but their top-level agreed amounts are immutable. Amounts use checked integer arithmetic.
Capacity can be zero; occupied/held seats are always protected. Names/descriptions have explicit
200/4000-character limits. UTC and MXN are required at boundaries.

Service quotes use a separate Reservations signing key, issuer tourlab-services, audience
catalog-internal, subject reservations, permission quote:create and at most two minutes of validity.
Only Catalog receives its public key. Gateway never receives this private key or invokes quotes.
There is no service token issuing HTTP endpoint. Test tooling signs locally using private lab files.
