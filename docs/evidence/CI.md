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
| actionlint 1.7.7 | Passed against `.github/workflows/ci.yml`; official release download verified against release SHA-256 checksums |
| PowerShell parser / diff whitespace | Passed |
| Initial live suite, concurrent image build | 65 passed, one failed, 29 intentionally skipped; unrelated timing finding below; private `.local/ci-live-initial.trx` |

The live rerun without concurrent image compilation also produced **65 passed, one failed, 29 intentionally skipped**, with the same 503-versus-504 failure (2m25s). Additional local suite and GitHub execution results will be recorded after completion. At this point no GitHub success is claimed. Authenticated read access to the repository and enabled Actions were verified; remote main matches the inspected local base.

## Unrelated finding — Gateway quote timeout boundary

The initial local live run failed `OperationsTests.SlowCatalogFiveFailuresOpenCircuitThenRecoveryCreatesWorkOnlyAfterQuoteSucceeds`: expected 503 ServiceUnavailable, actual 504 GatewayTimeout. No application or test behavior was changed to mask it. The generated Gateway YARP route uses a ten-second ActivityTimeout while Reservations has a ten-second total quote budget; the outer timeout can win before the dependency failure response reaches Gateway. A rerun without concurrent compilation reproduced the same failure. The cause is consistent with the observed outer timeout; no application fix is included. The test stays enabled and a failure blocks CI. The earlier 40-second typed-client fix does not itself increase YARP's route ActivityTimeout. Treat alignment of the outer proxy budget as a separate application change, with its own verification.

## Requirement-to-check map and gaps

| Requirement | Implementation / evidence |
|---|---|
| PR/main triggers, Linux/.NET10, minimal permissions, cancellation/time limits | `.github/workflows/ci.yml`; actionlint and verified action pins |
| Appropriate actual tests and disposable infrastructure | `scripts/Start-CI.ps1`, `Verify-CI.ps1`; existing Compose/provisioner/test gates |
| Reports on failure without secrets | `Write-CIReports.ps1`, `Test-CIReports.ps1`; actual failed local result and synthetic redaction check |
| Merge checks/manual settings/diagnosis | `docs/ci.md`; repository policy not changed |
| Preserve application behavior/data | No application code, test business assertions, migrations, queues or volumes removed/modified |

Browser Playwright, long session expiry/navigation, optional collector/viewer, cloud/Neon/IAM and merge-rule enforcement remain separate. GitHub run observation, fork approval behavior, cancellation under load and first push-to-main execution must not be inferred from local validation. No resource deployment or image push occurred.
