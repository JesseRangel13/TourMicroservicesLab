# Local interview demo: 10–15 minutes

Payments and email in every step are durable simulations. Use the running local PostgreSQL/ElasticMQ lab and preserved `.local` users. No AWS, real card, SMTP, queue purge or database deletion is involved. Browser steps require an operator; they are not reported as executed browser evidence.

| Time | Action and what to explain |
|---|---|
| 0–2 min | Sign in as Alice and Admin in separate browser profiles. Open a future active session, accept its price and request one seat. Follow reservation status/timeline to Confirmed, then inspect one simulated charge and a Sent fictional notification. Explain that Confirmed requires charge AND Catalog confirmation. |
| 2–3 min | Show the original intention key and the automated duplicate assertions below. Explain HTTP intention identity, message identity and provider operation identity separately. Repeated deliveries are permitted; a second effect is prevented by durable state. |
| 3–5 min | Admin `/operations`: Payments → Decline → one occurrence. Alice submits a NEW intention. Observe release/Compensating before Failed, no charge and restored seats. Reset Payments to normal. |
| 5–7 min | Payments → TimeoutAfterCharge → one occurrence. Create a NEW reservation. Inspect temporary Unknown followed by reconciliation/Confirmed; provider lookup uses the original ID and one durable fictional charge. Reset. |
| 7–9 min | Catalog → RejectNextConfirmation → one occurrence. Request a NEW reservation with normal simulated payment. Observe Compensating and independent refund/release progress; Failed only after both acknowledgements. Inspect the refund on payment detail. Reset Catalog. |
| 9–11 min | Cancel the first future Confirmed reservation. Observe CancellationPending → Cancelled with a full original-charge refund and release. Reload rather than inventing a new key after an uncertain response. Show Bob's 404 for Alice's details. |
| 11–13 min | Admin diagnostics: select each service, explain Outbox age, worker state and approximate DLQ counts. Refresh informational projection/version and explain why it cannot approve a price/allocate seats. DLQ receive changes visibility; replay preserves original IDs and may duplicate. Use the existing controlled replay test rather than replaying an uncorrected production-like message. |
| 13–15 min | Show the consolidated acceptance matrix and explain ManualReview, session revalidation, encrypted cookie persistence, bounded quote retries and the remaining browser/cloud verification gaps. Stop the application session when done; fault pause does not stop cloud billing or Neon worker activity. |

For concise reproducible invariant evidence, run this BEFORE the live browser demo (it temporarily pauses/recreates workers and restores them):

```powershell
./scripts/Verify-Sagas.ps1 -Filter 'FullyQualifiedName~CompleteSuccessAndDifferentMessageDuplicatesHaveOneChargeInventoryAndSimulatedDelivery|FullyQualifiedName~DeclineReleasesBeforeFailedAndDoesNotCharge|FullyQualifiedName~ProviderCommitCrashConcurrentRecoveryAndTimeoutLookupNeverChargeAgain|FullyQualifiedName~ConfirmationFaultRefundAndReleaseCompleteInEitherOrder|FullyQualifiedName~ConcurrentCancellationKeysOneRefundAndReleaseWithOwnershipAndDepartureChecks' -ResultFile 'interview-demo.trx'
```

These tests use real PostgreSQL/ElasticMQ and independently durable fake effects with controlled workers/crash barriers, not browser clicks or real external providers. Use `docs/operations-execution.md` for poison/redrive/replay and `docs/saga-execution.md` for UnknownUntilAdminResolution and compensation exhaustion. Never press a new-intention button merely because an accepted request timed out. Reset only configured fault modes; do not remove business evidence.
