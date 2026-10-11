# AWS operator scripts

Every script declares `#requires -Version 7.0`, including the shared helper. Invoke scripts with `pwsh`, not Windows PowerShell's `powershell.exe`. Older engines reject the script before parameter binding or execution, including scripts that do not load Shared.ps1.

Run from the repository root using PowerShell7. Scripts are deployment artifacts for LAB-009, not an automatic deployment pipeline. Default Start/Stop/Destroy/Migrations are previews. Preflight is offline unless `-ReadOnlyAws` is supplied. AWS operations validate study account, reject root credentials, enforce the exact prefix and ownership tags, and disable automatic CLI retries. Private diagnostics stay under ignored `.local`.

| Script | Purpose |
|---|---|
| New-PrivateConfiguration.ps1 | Generate a separate cloud CA/leaf/JWT/protection set and private direct/pooler connections; prompt for administrator credentials; no network connection |
| Publish-Parameters.ps1 | Offline size/bundle validation; explicit PARAMETERS confirmation to publish Standard SecureString; tagged ownership required to overwrite |
| Invoke-Migrations.ps1 | Preview by default; explicit MIGRATE confirmation runs the guarded external one-shot provisioner |
| Invoke-Session.ps1 | Offline/read-only preflight; explicit START/STOP confirmation; readiness and rollback; verify no running/pending tasks after stop |
| Verify-Readiness.ps1 | Verified-TLS Gateway login and readiness for all five hosts; private temporary auth files are removed |
| Review-Destroy.ps1 | Private state ownership review, stopped tasks, separate queue/image consent, saved destroy-plan SHA and delete-only scope validation |
| Test-Guards.ps1 | Offline malicious-scope and preview regression checks; no AWS access |

Read docs/aws-execution.md before using Execute. `-Overwrite` never rotates running task secrets automatically: restart explicitly after reviewing certificate/key compatibility. Stop verification waits up to five minutes plus bounded CLI calls; a failed stop is not proof of zero billing. No timer is promised. A scheduled local invocation depends on the PC, network and credentials remaining available.
