# Continuous integration

The workflow is `.github/workflows/ci.yml`. It runs for every pull request (including fork PRs) and push to `main`, without path filters. It uses `pull_request`, never privileged `pull_request_target`, and requests only `contents: read`. Checkout does not persist Git credentials. No repository secrets, AWS credentials, cloud database, registry login, image push or deployment are used. Standard GitHub-hosted `ubuntu-24.04` is selected; no larger runner or paid infrastructure is provisioned. This public repository can use standard hosted runners under GitHub's public-repository terms; check quotas/billing if visibility changes. See [official Actions billing terms](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

## Triggers and dependencies

One job has the stable check name **Build, tests and five images** and a 35-minute timeout. One job keeps the disruptive integration groups sequential, avoids duplicate image builds and does not require sharing private configuration or images through artifacts. Superseded runs for the same PR are cancelled. Each push to main uses its unique run ID as the concurrency key and disables automatic cancellation, preserving both running and pending audit builds. Merely setting cancel-in-progress to false with a shared main key would still permit replacement of an older pending run. Different PRs get different disposable runner VMs and Compose projects `tourlab-ci-{run_id}-{run_attempt}`. Fork PR approval policy remains controlled by the repository owner.

The ordered steps are:

1. Checkout; setup-dotnet reads the exact SDK in `global.json` (10.0.401). Locked solution restore and Release build enforce existing nullable/warnings-as-errors settings.
2. Verify report redaction, then run 29 current pure unit/contract/resilience/client-lifetime cases without enabling integration flags.
3. Generate fresh private configuration with the existing `Lab.Provisioner init`. Restrict the host `.local` directory, explicitly trust the generated public CA, and permit the service-specific read-only mounts to be read by non-root containers. No certificate validation bypass is used.
4. Reuse digest-pinned Compose PostgreSQL 17.6 and ElasticMQ 1.6.15. Bootstrap separate runtime/migrator roles, five schemas, checked-in migrations and configurable seeds. Initialize real queues/DLQs through the existing local SDK provisioner. Build all five independent Release Docker images and start HTTPS hosts.
5. `Verify-CI.ps1 -Suite Live`: the normal suite with `LAB_TEST_ROOT`, real database/API/security/ownership/resilience tests and live workers. Controlled/restart/limiter cases intentionally skip here.
6. `-Suite Controlled`: pause all four hosted consumers and payment/notification/Saga workers using the existing Compose switches. Enable messaging/payment test flags; run MessagingTests, PaymentWorkflowTests, SagaWorkflowTests and the four controlled OperationsTests. Each dedicated class group starts fresh paused business-service containers, resetting only volatile limiter/breaker state. This prevents the combined batch from exhausting the real Admin limit on fast Linux. Existing test-generated unique sessions, operation IDs and evidence queues are reused; no database/queue reset, deletion or purge is added. All four group outcomes are retained even if one fails. Restore workers in `finally`.
7. `-Suite Restart`: pause workers and enable the existing actual Compose stop/start test. It checks retained snapshots, encrypted login cookie, broker message and independent durable effects. Restore workers afterward.
8. `-Suite RateLimit`: run the intentionally credential-window-exhausting test last. No later login-dependent suite follows it.
9. Always construct safe reports, append the summary, upload `test-reports` for seven days, and stop only this run's containers without deleting volumes. Runner disposal removes the disposable environment; the scripts do not delete local or business data. Cancellation/runner termination can bypass finally/cleanup; hosted runner disposal remains the isolation boundary.

The last four steps have bounded step timeouts. A failed mandatory step fails the job; report upload cannot turn it green. Once provisioning succeeds, controlled/restart/limiter groups still run after a test failure so that their independent results are available; they do not use continue-on-error. Tests must execute at least one case, and non-Live suites reject unexpected skips. Unit cases also run within the normal suite, so do not add suite counts to claim unique coverage. The messaging outage harness uses the portable Compose `up -d --wait` restoration; older hosted Compose versions do not support `start --wait`. Business assertions are unchanged. Current unique backend coverage is 95 cases: 66 normal, 27 controlled, one restart and one limiter. Future cases require updating dedicated filters if they use a new controlled test gate.

## Results and diagnosis

Open the PR Checks tab or Actions → CI → run → **Build, tests and five images**. Restore/build failures are visible at their named step. Test steps print only counters and a safe failure message. The job summary and `test-reports` artifact contain method identifiers, outcomes, durations, assertion categories and allowlisted HTTP status names in Markdown, JSON and JUnit XML. Theory cases are numbered; argument values are deliberately omitted. Reports are uploaded even after test failure. If execution fails before tests, the summary explicitly says no report exists; this is not a test pass.

Raw console logs/TRX, assertion messages, stack traces, request bodies, tokens, cookies, connection strings, private configuration and certificates are **not artifacts**. The converter constructs new allowlisted documents; it does not copy raw TRX. `Test-CIReports.ps1` verifies that a failed test's fictional secret in display name/stdout/message/stack does not escape. A raw test-runner or container log should not be added as an upload target. Docker context ignores private configuration, keys, test outputs and browser reports.

For a failed method, reproduce with the same commit locally. Existing Windows setup remains supported:

```powershell
dotnet restore TourMicroservicesLab.slnx --locked-mode
dotnet build TourMicroservicesLab.slnx -c Release --no-restore
./scripts/Test-CIReports.ps1
./scripts/Verify-CI.ps1 -Suite Unit
# Start/configure/trust the existing local lab using README first.
./scripts/Verify-CI.ps1 -Suite Live
./scripts/Verify-CI.ps1 -Suite Controlled
./scripts/Verify-CI.ps1 -Suite Restart
./scripts/Verify-CI.ps1 -Suite RateLimit # last; consumes the credential window
```

These integration groups interrupt the local lab temporarily; avoid running during a demo. Raw diagnostics stay ignored under `TestResults/ci/private`. Inspect them privately. To narrow a failing case, set the appropriate environment flags from Verify-CI and use `dotnet test tests/Lab.Tests/Lab.Tests.csproj -c Release --no-build --filter FullyQualifiedName~MethodName`; controlled cases require the same worker pause/restoration. A Linux-only failure also needs Linux reproduction or a new GitHub run; a Windows pass is not Linux proof. Start-CI deliberately refuses the existing PC's private environment.

## Required checks and manual repository settings

Configure these manually in GitHub; this implementation does not change repository policy:

- Enable Actions and permit the SHA-pinned `actions/checkout`, `actions/setup-dotnet` and `actions/upload-artifact`. Pins were verified against the official repositories' tags and commit APIs; versions/SHAs are annotated in YAML. Review upstream releases before changing them.
- Under a ruleset or branch protection for `main`, require a pull request and the **Build, tests and five images** check from GitHub Actions. Require the branch to be up to date if desired. This one required check gates restore, Release build, unit/live/controlled/restart/limiter tests and five image builds together. Require reviewer approval according to your team policy, especially for workflow changes.
- Keep default workflow token permissions read-only; do not grant PR workflows secrets or write access. Choose the appropriate approval policy for first-time/fork contributors. Approve only code you have reviewed: untrusted PR code executes on an isolated hosted runner.
- Keep the standard hosted runner and retention settings. No secrets need configuring. Do not add cloud connection strings to CI. If using a merge queue later, add the `merge_group` trigger before making this check mandatory for the queue.

Browser Playwright, real thirty-minute browser expiry, measured browser navigation cancellation, optional Jaeger export and AWS/Neon behavior are outside this workflow. The LAB-007 browser suite remains runnable locally; its prior 2/2 result does not imply it ran in CI. This task does not implement LAB-008 or LAB-009.

## Validation references

See [actual evidence](evidence/CI.md), which distinguishes local results from observed GitHub runs. GitHub semantics follow the official [workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax), [concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency) and [secure action pinning guidance](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#using-third-party-actions). Actions are maintained in [checkout](https://github.com/actions/checkout), [setup-dotnet](https://github.com/actions/setup-dotnet) and [upload-artifact](https://github.com/actions/upload-artifact).
