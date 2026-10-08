# GitHub Actions CI evidence

Date: October 8, 2026 (America/Mexico_City). Scope: CI only, existing LAB-001–007 implementation. No LAB-008/009, cloud database, image publication, AWS deployment or paid resources.

## Inspection and implementation

Inspected AGENTS.md, README, specifications 01–07, preceding implementation/verification evidence and independent reviews, all verification scripts, the nine-project solution, test gates and dependencies, provisioner, Compose and Dockerfile. The newest LAB-007 independent review records PASS; older implementation-stage browser gaps remain historical and were not rewritten by this task. The user's unrelated `docs/interview-study-guide.md` is preserved.

One Linux job sequentially performs locked restore, Release build, unit tests, isolated local provisioning, all five image builds, live and controlled real PostgreSQL/ElasticMQ checks, actual restart and limiter checks. CI reuses existing runtime/migrator schema separation, configurable seed generation, TLS, unique evidence queues/operations, worker switches and test classes. Private output is reconstructed into safe JUnit/JSON/Markdown reports; no raw TRX/log/config/key/certificate is uploaded.

Action pins verified October 8 through official GitHub tag and commit APIs:

| Action | Tag | Verified commit |
|---|---|---|
| actions/checkout | v4.3.1 | 34e114876b0b11c390a56381ad16ebd13914f8d5 |
| actions/setup-dotnet | v5.0.0 | d4c94342e560b34958eacfc5d055d21461ed1c5d |
| actions/upload-artifact | v4.6.2 | ea165f8d65b6e75b540449e92b4886f43607fa02 |

## Actual local validation

Environment: Windows PowerShell 7, .NET SDK 10.0.401, Docker Desktop Linux 29.6.2; existing real PostgreSQL/ElasticMQ and HTTPS hosts. This is not a GitHub-hosted Linux result.

| Check | Actual result |
|---|---|
| Locked solution restore | Passed; private `.local/ci-restore.log` |
| Release solution build | Passed, zero warnings/errors, 21.11 seconds; private `.local/ci-build.log` |
| Safe report failure/pass/skip fixture | Passed; fictional sensitive display-name/stdout/error/stack content omitted, failure preserved |
| Unit/contract/resilience/client-lifetime suite | 29 passed, zero failures/skips |
| All five Docker images | Built successfully with locked restore and Release publish; private `.local/ci-images.log` |
| Actual stop/start persistence | One passed, zero failures/skips, 1m32s; all five hosts restored healthy |
| Credential limiter (last) | One passed, zero failures/skips |
| Controlled messaging/payment/Saga/operations suite | 27 passed, zero failures/skips, 3m22s |
| Start-CI refusal and missing-report handling | Passed: existing Windows lab refused; no-report summary explicitly generated |
| actionlint 1.7.7 | Passed against `.github/workflows/ci.yml`; official release download verified against release SHA-256 checksums |
| PowerShell parser / diff whitespace | Passed |
| Initial live suite, concurrent image build | 65 passed, one failed, 29 intentionally skipped; unrelated timing finding below; private `.local/ci-live-initial.trx` |

The live rerun without concurrent image compilation also produced **65 passed, one failed, 29 intentionally skipped**, but with a different failure: ConcurrentOwnersCannotReadFilterOrCancelEachOthersDurableResources expected 202 Accepted and received 503 ServiceUnavailable (2m25s); the slow-Catalog test passed in that run. Additional local suite and GitHub execution results will be recorded after completion. At this point no GitHub success is claimed. Authenticated read access to the repository and enabled Actions were verified; remote main matches the inspected local base.

## Observed GitHub execution

[Run 37856036467](https://github.com/JesseRangel13/TourMicroservicesLab/actions/runs/37856036467), PR #1, commit e6527d8, completed with **failure**. Actual hosted Linux results: locked restore/Release build, report redaction, 29 unit cases, isolated TLS/PostgreSQL/ElasticMQ provisioning and five images passed; live suite 66 passed / 29 deliberate skips; controlled suite 20 passed / seven failed; restart and limiter each passed. Safe report upload and run-specific container stop passed. The seven downloaded files were inspected and contained only allowlisted report formats and fields. Independent suites continued after the controlled failure and the job correctly stayed red.

Controlled failures were five MessagingTests (claim fencing, rollback, real Catalog outage, send-before-mark, commit recovery), ProviderCommitCrashConcurrentRecoveryAndTimeoutLookupNeverChargeAgain, and PaymentPauseConfigurationDoesNotRewriteAcceptedProviderMode. Their Windows equivalents passed. A follow-up run adds finite-allowlist HTTP status names and assertion categories to safe reports to distinguish Linux dependency/test isolation causes without exporting raw diagnostics. No successful full GitHub run is claimed at this stage.
[Run 37856758828](https://github.com/JesseRangel13/TourMicroservicesLab/actions/runs/37856758828), commit 30436cd, also completed with failure: the same 20/7 controlled results, all other steps passed. Its allowlisted diagnostics identified two HTTP 429 Admin-throttle failures and four HTTP 503 booking failures following the outage test. The runner image's official software inventory declares Docker Compose 2.38.2; its [start command source](https://github.com/docker/compose/blob/v2.38.2/cmd/compose/start.go) does not expose --wait. Local Docker Desktop uses Compose 5.3.1, which accepts it.

CI corrections: controlled Messaging/Payments/Sagas/Operations groups now run separately with fresh paused business containers per group (data retained, real limits unchanged). The outage test restores Catalog with supported `up -d --wait`, preserving the actual outage/no-work assertions. No runtime rate-limit override, retry of a payment effect, test exclusion or weakened business assertion was introduced. These harness corrections will be verified in another actual Linux run.
## Unrelated finding — Gateway quote timeout boundary

The initial local live run failed `OperationsTests.SlowCatalogFiveFailuresOpenCircuitThenRecoveryCreatesWorkOnlyAfterQuoteSucceeds`: expected 503 ServiceUnavailable, actual 504 GatewayTimeout. No application or test behavior was changed to mask it. The generated Gateway YARP route uses a ten-second ActivityTimeout while Reservations has a ten-second total quote budget; the outer timeout can win before the dependency failure response reaches Gateway. A rerun without concurrent compilation passed this slow-Catalog test but failed reservation creation in the ownership test with 503 instead of 202. The initial 504 is consistent with the outer timeout boundary, but its repeatability and the second failure cause are not established. No application fix is included. The test stays enabled and a failure blocks CI. The earlier 40-second typed-client fix does not itself increase YARP's route ActivityTimeout. Treat alignment of the outer proxy budget as a separate application change, with its own verification.

## Requirement-to-check map and gaps

| Requirement | Implementation / evidence |
|---|---|
| PR/main triggers, Linux/.NET10, minimal permissions, cancellation/time limits | `.github/workflows/ci.yml`; actionlint and verified action pins |
| Appropriate actual tests and disposable infrastructure | `scripts/Start-CI.ps1`, `Verify-CI.ps1`; existing Compose/provisioner/test gates |
| Reports on failure without secrets | `Write-CIReports.ps1`, `Test-CIReports.ps1`; actual failed local result and synthetic redaction check |
| Merge checks/manual settings/diagnosis | `docs/ci.md`; repository policy not changed |
| Preserve application behavior/data | No application code, test business assertions, migrations, queues or volumes removed/modified |

Browser Playwright, long session expiry/navigation, optional collector/viewer, cloud/Neon/IAM and merge-rule enforcement remain separate. GitHub run observation, fork approval behavior, cancellation under load and first push-to-main execution must not be inferred from local validation. No resource deployment or image push occurred.
