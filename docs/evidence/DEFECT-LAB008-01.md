# LAB-008 cold-start timing observation

Date2026-10-10. Status: observed locally; deployment-sizing gate remains open.

Five Linux services ran with the optional PEM injection path,0.5CPU/1GiB quotas and existing local PostgreSQL/ElasticMQ. Normal suite76passed; Payments5passed and Operations4passed. After recreating paused business containers, the first reservation creation timed out at the test client's10second limit in:

- MessagingTests.ConsumerCommitBeforeAckCrashRedeliversWithoutRepeatingInventoryOrSagaTransition.
- SagaWorkflowTests.AvailabilityDeadlineReleaseBeforeHoldAndLateHeldNeverStartPayment.

Both stack traces stop at MessagingTests.CreateAsync, before the intended crash/late-message assertions. The complete capped controlled run was25passed/2failed. Twelve samples in the earlier normal run reached roughly the0.5CPU quota; memory stayed below107MiB for all services.

Comparison kept PEM inputs, database, broker and1GiB memory limits, removed only the CPU quota and recreated business services. Messaging then passed8/8. Full comparison results are in LAB-008.md. This narrows the concern to cold-start timing/resource pressure; it does not prove cloud capacity or rule out all scheduling effects.

No test timeout, money/inventory rule or local Gateway timeout was changed. The existing local proxy/client10second boundaries and cold model/JIT initialization need deliberate sizing/startup investigation. The cloud proxy40second activity budget avoids the known local10/10 quote-budget boundary, but cannot substitute for measurement. Start readiness exercises owned API queries before the student books; its real cloud cold-start behavior is unverified. Terraform permits reviewed0.5CPU with1/2GiB or1CPU with2GiB; recompute costs for changed sizes and obtain cold-start evidence before deployment. Do not describe the cheapest profile as proven adequate.
