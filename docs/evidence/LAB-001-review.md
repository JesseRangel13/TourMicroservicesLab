# LAB-001 Independent Review Report

**Date**: October 3, 2026  
**Subject**: TourMicroservicesLab — LAB-001 Local Foundation  
**Target Solution**: `TourMicroservicesLab.slnx` (.NET 10.0.401, PostgreSQL 17.6, ElasticMQ 1.6.15)  

---

## 1. Verdict

**PASS (with deferred verification and non-blocking follow-up)**

The foundation scaffold, project boundaries, local Docker infrastructure, schema and role isolation, local TLS certificate generation, Identity user seeding, secure cookies, and restart persistence meet all specified acceptance and exit criteria for LAB-001.

Neither the deferred rate-limiter ingress consideration nor the browser-automation verification limitation blocks progression to **LAB-002**, provided no other blocking defects exist.

---

## 2. Acceptance Matrix

| Requirement / Acceptance Criterion | Status | Evidence / Verification Notes |
|---|---|---|
| **Host projects exist & target .NET 10** | **PASS** | `Gateway.Web`, `Catalog.Api`, `Reservations.Api`, `Payments.Api`, and `Notifications.Api` target `net10.0` via `Directory.Build.props` and pin SDK `10.0.401` in `global.json`. |
| **Architectural boundaries & no shared entities** | **PASS** | `Shared.Contracts` contains only integration DTO `ServiceStatus.cs`. `Shared.Infrastructure` contains only hosting, JWT validation, and options validation. No shared EF entities, cross-service queries, or shared DbContexts exist. |
| **Reproducible build & locked dependencies** | **PASS** | `Directory.Packages.props` manages versions centrally; `packages.lock.json` committed for every project. `dotnet restore --locked-mode` and `dotnet build --no-restore` pass with zero warnings and errors. |
| **Local infrastructure (PostgreSQL & ElasticMQ)** | **PASS** | `compose.yaml` defines PostgreSQL 17.6 and ElasticMQ 1.6.15 pinned by digest. SQS standard queues configured with 60s visibility, 20s long polling, DLQs (`maxReceiveCount=5`), and H2 durable storage on named volumes. |
| **Container security & health** | **PASS** | All 5 host images build via multi-stage `Dockerfile` with non-root user `User=app`. All ports bind exclusively to loopback (`127.0.0.1`). HTTPS liveness healthchecks report healthy. |
| **PostgreSQL schema & role isolation (AC-25)** | **PASS** | 5 isolated schemas (`catalog`, `reservations`, `payments`, `notifications`, `identity`). Each has dedicated `_runtime` and `_migrator` logins. Integration test `EveryRuntimeRoleCanReadOwnSchemaButCannotReadWriteOrCreateElsewhere` verified that runtime logins can read their own schema while 40 cross-schema SELECT/UPDATE operations and public/schema DDL are rejected with SQLState 42501 (`InsufficientPrivilege`). |
| **Privilege separation & credential protection** | **PASS** | Privileged administrator and migrator credentials exist only in `.local/provisioner.json`. Runtime containers receive only their schema's runtime JSON config containing `{schema}_runtime` credentials. |
| **EF Identity migrations & idempotent seeding (AC-01)** | **PASS** | Checked-in PostgreSQL migration `20261003155022_InitialIdentity.cs` applied by `identity_migrator` before hosts start. `Alice`, `Bob`, and `Admin` seeded idempotently with Identity password hashes. Re-running bootstrap preserves existing users and passwords without data resets. `dotnet ef migrations has-pending-model-changes` reports 0 pending changes. |
| **Authentication & cookies (AC-27)** | **PASS** | SSR login form with antiforgery token via `AuthEndpoints.cs`. Secure cookie `__Host-tourlab` uses `Secure`, `HttpOnly`, `SameSite=Strict`, 30-minute lifetime, and `SlidingExpiration=false`. Failed logins redirect with generic `InvalidCredentials` without sensitive leaks. Token endpoint `/auth/token` checks credentials and issues JWTs strictly reflecting database claims; clients cannot supply arbitrary roles or subjects. |
| **Data Protection key encryption & persistence** | **PASS** | Keys persist in `identity."DataProtectionKeys"` and are encrypted at rest using certificate `protection.pfx`. `PersistenceTests` verified that session cookies survive full Docker Compose stop/start cycles. |
| **Blazor navigation shell & Gateway** | **PASS** | Navigation shell implemented in Blazor Web App (.NET 10) with `MainLayout.razor` and `NavMenu.razor`. Interactive Server used on `Account.razor`. `LabRevalidatingAuthenticationStateProvider` revalidates identity every 30 seconds via short-lived async scopes. No DbContext is injected into or retained by Blazor components. |
| **Interactive Circuit Browser UI Verification** | **NOT VERIFIED** | **Verification limitation, not a confirmed application defect.** The Windows browser sandbox failed to initialize (`helper_unknown_error: apply deny-read ACLs`). HTTP verification verified SSR form markup, anti-CSRF cookies, redirect behavior, and rendered account HTML, but does not substitute for interactive circuit and browser event execution. |
| **YARP reverse proxy & route isolation** | **PASS** | Routes `/api/{service}/v1/{**rest}` forward over TLS to internal APIs with JWT validation. Explicit literal handler maps `/api/{service}/v1/internal/{**rest}` to 404 before reaching YARP, blocking internal endpoints. |
| **TLS & certificate validation** | **PASS** | Private CA and leaf certificates generated via RSA 3072/2048. PostgreSQL requires TLS (`hostssl ... scram-sha-256`, `hostnossl ... reject`) with `VerifyFull`. Test client configures `X509ChainPolicy.CustomTrustStore` against `ca.crt` and verifies full chain and hostname match without disabling validation. |
| **DI lifetimes & safety** | **PASS** | No singleton captures a scoped `DbContext`. `AddDbContextFactory<IdentityDb>` is used for Identity operations. No `DbContext` is retained for circuit lifetimes. No fire-and-forget background tasks. |

---

## 3. Findings and Dispositions

### Finding 1: Lack of initial Git commit baseline
- **Severity**: Low (Repository hygiene / Tracking)
- **Location**: Repository root (`.git/`)
- **Observed**: Repository was initialized with `git init` on branch `master`, but had zero commits; all project files were untracked.
- **Disposition**: **RESOLVED**. Baseline commit created on branch `main` (`feat(lab-001): scaffold local foundation, services, compose, and auth`) and pushed to remote `https://github.com/JesseRangel13/TourMicroservicesLab`. Private `.local` files remain excluded via `.gitignore`.

### Finding 2: Gateway rate-limiter client partitioning
- **Severity**: Low (Deferred deployment consideration)
- **Location**: `src/Gateway.Web/Program.cs#L35-L37`
- **Observed**:
  ```csharp
  context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
  ```
  The fixed-window rate limiter for credential endpoints (`/auth/login`, `/auth/token`) partitions by `RemoteIpAddress`.
- **Classification & Disposition**:
  - **Non-blocking for LAB-001**: In the local single-instance Docker/loopback lab, direct connections accurately report the client IP.
  - **Deferred deployment consideration**: Conditional on the actual ingress topology.
  - **Not a confirmed defect in the current direct-access configuration**: The initial low-cost AWS profile specifies direct access to Gateway without an Application Load Balancer (ALB). Containerization alone does not establish that an upstream reverse proxy changes the client IP.
- **Follow-up Plan (recorded under LAB-007 / LAB-008)**:
  - If an upstream reverse proxy or ALB is introduced, configure trusted forwarded headers before rate limiting.
  - Specify explicit trusted proxies/networks (e.g. VPC CIDR or known proxy hops) and thoroughly test client-IP extraction.
  - Do not accept arbitrary client-supplied `X-Forwarded-For` headers.
  - Do not present adding `app.UseForwardedHeaders()` alone as a complete fix without configuring `ForwardedHeadersOptions.KnownProxies` / `KnownNetworks`.

---

## 4. Verification Limitations & Follow-Up

### Browser UI Automation (Playwright / Computer-Use)
- **Status**: **NOT VERIFIED**
- **Reason**: The Windows browser/computer-use automation helper failed to initialize due to a local sandbox permission error (`windows sandbox failed: helper_unknown_error: apply deny-read ACLs`).
- **Nature**: This is an environmental verification limitation, **not** a confirmed application defect.
- **Evidence Boundary**: HTTP tests verified SSR form markup, anti-CSRF cookies, redirect behavior, and rendered account HTML. This evidence is preserved as valid HTTP-level verification, but is not treated as proof of interactive Blazor circuit establishment or multi-user DOM state handling.
- **Immediate Follow-up**: **COMPLETED & VERIFIED**. Manual smoke testing of login, navigation, role authorization, and logout was executed and confirmed by the operator on October 3, 2026.
- **Formal Automated Follow-up**: Automated interactive-circuit establishment, multi-user circuit isolation (AC-26), and browser UI E2E are scheduled for implementation and verification in **LAB-007**.

---

## 5. Executed Checks Summary

| Check / Command | Expected | Actual Result | Status |
|---|---|---|---|
| `dotnet restore --locked-mode` | Restore dependencies against lock files | Restored 9 projects | **PASS** |
| `dotnet build --no-restore` | Build with warnings as errors | Succeeded, 0 warnings, 0 errors | **PASS** |
| `dotnet ef migrations has-pending-model-changes --project src/Gateway.Web` | No pending migrations | No changes made to model since last migration | **PASS** |
| `docker compose config --quiet` | Valid compose specification | Exited with code 0 | **PASS** |
| `docker image inspect ... --format '{{.RepoTags}} User={{.Config.User}}'` | Images run as non-root user `app` | All 5 images report `User=app` | **PASS** |
| `docker compose ps` | Containers running and healthy | All 5 hosts, postgres, and elasticmq healthy | **PASS** |
| `dotnet list package --vulnerable --include-transitive` | Package vulnerability audit | 0 vulnerable packages across all 9 projects | **PASS** |
| `scripts/Verify-Local.ps1` | Integration test suite | 19 passed, 0 failed, 1 skipped (restart test) | **PASS** |
| `scripts/Verify-Restart.ps1` | Restart persistence check | 1 passed, 0 failed, 0 skipped | **PASS** |
| `curl.exe --ssl-revoke-best-effort --cacert .local/certificates/ca.crt https://localhost:8443/health/live` | Alive status over TLS | Returned `{"status":"Alive"}` | **PASS** |
| `curl.exe ... https://localhost:8443/unknown-path` | RFC 9110 ProblemDetails 404 | Returned `404 Not Found` with `code="HttpError"` | **PASS** |
| `curl.exe ... https://localhost:8443/` | Blazor SSR HTML shell | Returned 200 OK with prerendered navigation shell | **PASS** |

---

## 6. Handoff to Codex

LAB-001 provides a validated foundation for **LAB-002**.

Neither the rate-limiter client partitioning finding (classified as a deferred, topology-conditional follow-up for LAB-007/008) nor the browser UI automation limitation (deferred to LAB-007 with manual smoke check in the interim) blocks starting **LAB-002**.

Priorities for LAB-002:
1. **Catalog Domain & Persistence**: Create `CatalogDbContext` in `Catalog.Api` under schema `catalog` and generate migrations with `catalog_migrator`.
2. **Contracts & DTOs**: Define catalog query and command contracts in `Shared.Contracts` per `specs/03-contracts.md`.
3. **Internal Quotes & Atomic Inventory**: Implement `POST /v1/internal/quotes` with requestId idempotency, price validation, and seat hold management.
4. **Catalog UI**: Implement tour browsing and admin session management in `Gateway.Web`.
