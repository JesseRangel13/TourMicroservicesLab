# Local scripts

Run PowerShell 7 scripts from this repository. Each script sets its working directory to the root.
They operate only on the `tourlab-local` Compose project; none call AWS or purge data.

| Script | Purpose |
|---|---|
| New-LocalConfiguration.ps1 | Restrict `.local` ACL to current Windows user/SYSTEM, then generate passwords and a private CA/leaf certificates. Refuses to overwrite existing settings. |
| Trust-LocalCa.ps1 | Install only this lab's public CA in the current user's Windows Root store. |
| Start-Local.ps1 | Start PostgreSQL/ElasticMQ; run one-shot roles/migrations/seeding; build and start five hosts. `-SkipBuild` reuses existing images. |
| Stop-Local.ps1 | Gracefully stop containers, preserving named volumes and private settings. |
| Verify-Local.ps1 | Locked restore, build, and real local database/HTTP checks. |
| Verify-Restart.ps1 | Explicit stop/start verification of database, queue message, and cookie persistence. |
| Start-CI.ps1 | Fresh GitHub-hosted Linux runner only: private configuration, trusted CA, isolated Compose project, owned migrations/queues, five image builds and HTTPS startup. Refuses existing `.local`. |
| Verify-CI.ps1 | Release/no-build test groups: Unit, Live, Controlled, Restart, RateLimit. Captures raw output privately, restores worker configuration in finally and produces safe reports. |
| Write-CIReports.ps1 | Reconstruct allowlisted JUnit/JSON/Markdown reports from private TRX, without diagnostic text, parameters, credentials or paths. |
| Test-CIReports.ps1 | Fictional sensitive-data failure fixture verifies pass/fail/skip reporting and redaction. |

Only the one-shot provisioner reads `.local/provisioner.json`. Review/configure seeded users privately
before first bootstrap. Runtime mount lists are intentionally service-specific. The CA private key
is never mounted into any container. Keep `.local` backed up securely if preserving data/cookies;
losing its keys/passwords makes the persisted environment unusable without a deliberate recovery.

To remove trust when finishing the lab, use the thumbprint printed by Trust-LocalCa and remove **that
specific** certificate from the current user's trusted root store. This is separate from pausing.
Do not delete unrelated certificates or volumes.

# LAB-007 security/browser checks

`Verify-Security.ps1` selects real HTTPS/Identity/schema/ownership checks and per-request client isolation tests after building and starting the local lab. `Verify-Security.ps1 -RateLimitOnly` should run last: it uses nonexistent users and consumes the shared login/token window for at most one minute. `Verify-Browser.ps1 -ListOnly` restores pinned dependencies and discovers tests without launching a browser; omit the switch to use installed Chrome with the trusted lab CA. Never add certificate bypasses. Details and manual checks: `docs/security-execution.md`.
