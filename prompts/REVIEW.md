# Independent Review Prompt

Review the TourMicroservicesLab implementation against specs/ and task evidence. Do not modify code during this first review. Focus on concurrent idempotency, inventory, commit before ACK, provider effects before responses, durable Saga state, expiration/late success, Inbox and Outbox atomicity, stable identifiers, schema isolation, resource authorization, worker/Blazor scopes, cross-user token leakage, TLS/secrets, Terraform costs, and cleanup.

Run available tests and search for cases that contradict invariants, not only existing test coverage. Report prioritized findings with the file, behavior, reproducible scenario, and suggested fix. Distinguish confirmed failures from unverified risks. Do not assume exactly-once delivery or free Fargate hosting. Do not claim verified deployment without access and actual evidence. Deliver a matrix of implemented/verified/pending requirements and a concrete next-task recommendation. Write the entire review in English.
