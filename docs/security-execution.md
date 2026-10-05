# LAB-007 local security and browser verification

Run from `C:\Users\JesseRangel\Documents\ChatGPT\Tour Microservices`. Reuse the existing `.local` configuration, PostgreSQL volumes and queues. This guide does not deploy anything.

```powershell
./scripts/Start-Local.ps1 # rebuild after code changes
./scripts/Verify-Local.ps1
./scripts/Verify-Security.ps1
./scripts/Verify-Restart.ps1 # interrupts only this local Compose lab
./scripts/Verify-Security.ps1 -RateLimitOnly # run last: temporarily consumes the credential window
```

The rate-limit check uses nonexistent accounts to avoid locking Alice/Bob/Admin. Login and token issuance share ten attempts per minute per direct remote IP, per Gateway instance. Wait one minute before another login demonstration. Changing untrusted `X-Forwarded-For` cannot change that partition. Direct Gateway access has no upstream ALB/proxy; Docker alone does not justify forwarded-header processing. If LAB-008 introduces a proxy, require explicit trusted addresses/networks, limited hops, middleware before limiting, and a real topology test. That conditional configuration is NOT VERIFIED here.

## Authentication boundaries

Human bearer authentication checks RS256 signatures, issuer `tourlab-identity`, audience `tourlab-api`, expiration/not-before with zero clock skew, declared lifetime at most fifteen minutes, one nonblank bounded `sub`, and unique supported Tourist/Admin role claims. Roles and owners never come from body fields or arbitrary headers. Catalog's separate scheme checks the dedicated Reservations public key, `tourlab-services`, `catalog-internal`, one subject and a lifetime of at most two minutes. Its internal quote policy additionally requires `sub=reservations` and a single `quote:create` permission. Service tokens cannot authenticate human routes; human tokens cannot authenticate quotes. YARP never exposes internal routes.

The private human signing key and Data Protection encryption certificate are mounted only into Gateway. The private service signing key is mounted only into Reservations. Public verification keys are safe for the receiving hosts. The one-shot provisioner creates/owns local private configuration; it is never supplied to a business task. Local filesystem owners/Docker administrators are trusted operators, not isolated application tenants. Do not grant other machine users access to `.local`, publish its files, or upload raw logs/test artifacts. JWT-bearing and authentication request paths are excluded from verbose framework request/token logging; application errors log type/correlation only.

Cookies are Secure, HttpOnly, SameSite Strict, non-sliding and thirty minutes long. Encrypted Data Protection XML persists in Identity PostgreSQL. Restart verification preserves a real cookie across an actual stop/start; the expired-ticket test uses this same protected key ring, without a public test endpoint or plaintext keys. Logout changes the user's security stamp, affecting their other sessions too. HTTP stamp validation is periodic (one minute), while an existing Interactive Server circuit revalidates every thirty seconds. Clients additionally check the session expiration before issuing each request. Already-issued API-tool JWTs remain valid until their fifteen-minute expiration; logout does not implement a JWT revocation list. No claim is made that this issuer implements OAuth/OIDC.

SSR login/logout forms validate antiforgery tokens. Business APIs accept bearer authentication only, including at Gateway: a cookie alone cannot perform mutations. Interactive events belong to the authenticated circuit; the browser does not supply the outgoing bearer token or an owner. Same-origin, Strict cookies, the framework circuit connection and form antiforgery remain enabled. No permissive CORS or TLS bypass was introduced.

Identity stores exist only in short HTTP/tool/revalidation scopes, using a factory-created context where appropriate. Circuit clients hold AuthenticationStateProvider, not an authoritative HttpContext, DbContext, token or response cache. Every call reads current state and creates its own request/header. Singleton workers use async scopes; signing verification resources are DI-owned and disposed at shutdown. Polling tasks are owned/joined on disposal, stop on terminal/error state, and resume after an explicit successful reload. The component cancellation test exercises outstanding asynchronous work; it does not prove browser navigation or every process shutdown interleaving.

## Human-operation coverage and specification interpretation

Specification 03 calls `/auth/token` an API-tool credential login, while specification 05 forbids secrets in the UI. Preserve that boundary: the Blazor login supplies the credential sign-in operation, Account supplies identity/role inspection, and API tools use the JSON credential endpoint directly. Do not add a browser token display/storage/export control to satisfy a literal endpoint-for-button count. This is an explicit interpretation of those two requirements, not an invented name/role token issuer. `/health` and scaffold `/status` are operational probes rather than additional human business operations.

| Human contract / operation | Existing Blazor page or control |
|---|---|
| Credential login, logout, current identity | `/login`, `/logout` SSR forms; `/account`; `/auth/token` remains the documented API-tool equivalent |
| Tour search/pagination/detail/sessions | `/tours`, `/tours/{id}` |
| Create/update/activate tour; add session; price/capacity/version conflict | `/admin/catalog`, `/admin/catalog/{id}`, `Components/Catalog/SessionEditor.razor` |
| Create reservation and retry original intention | `Components/Reservations/ReservationSubmission.razor` on tour detail; frozen original price/quantity/key |
| Own/Admin-filtered reservations, detail, timeline, cancellation | `/reservations`, `/reservations/{id}`; cancellation retains its intention key |
| Payment list/filter/detail, refund progress, Unknown reconciliation, fake-provider resolution | `/payments`, `/payments/{id}`; Admin-only reconcile/charged/not-charged controls |
| Notification list/filter/detail and simulated content | `/notifications`, `/notifications/{id}` |
| Saga state filter/deadlines/flags and missing-compensation retry | `/admin/sagas` |
| Own-service diagnostics/pagination/fault selection/reset/DLQ receive/replay | `/operations`; explicit Interactive Server render mode; audited backend authorization |
| Projection/version/update time/pagination | `/tour-projections`; explicit Interactive Server render mode; no inventory authority |

No control fabricates a successful payment or integration event. DLQ inspection changes broker visibility and replay can duplicate delivery. Loading, typed errors/conflicts, pending/ManualReview flags and simulation labels remain visible. Use API tests for backend ownership/roles; hiding a control is insufficient.

## Browser automation and manual checklist

The single LAB-007 browser-tool reassessment returned **`trusted Node process exited unexpectedly; kernel reset, rerun your request`** before it could read any surface. Earlier LAB-001 evidence recorded **`helper_unknown_error: apply deny-read ACLs`**. The current failure does not prove the same cause; neither is proof of an application defect. No repeated setup retries, OS ACL changes, insecure browser arguments or certificate bypass were used. At that initial implementation stage browser clicks/hydration/circuits were **NOT VERIFIED**. Subsequent CLI Playwright execution resolved DEFECT-LAB007-01 and passed both focused browser tests (45.4 seconds); see [actual follow-up evidence](evidence/DEFECT-LAB007-01.md). Complete failure/recovery coverage, thirty-minute browser expiration and measured navigation cancellation remain outstanding.

Runnable local tests are `tests/EndToEnd/security.spec.js`, pinned to Playwright 1.63.0 with a lockfile. They use independent Alice/Bob/Admin contexts, real HTML forms, interactive reservation actions, two-user lists/404, cancellation, diagnostic/projection button effects, forged forms, cookie flags and cross-tab logout revalidation. Test discovery and syntax checks can run without launching a browser. They are focused coverage, not a claim that every failure/recovery screen is automated.

On the PC, trust the lab CA with `./scripts/Trust-LocalCa.ps1`, have Google Chrome installed and the local hosts running, then:

```powershell
./scripts/Verify-Browser.ps1 -ListOnly
./scripts/Verify-Browser.ps1
```

The script supplies the lab CA to Node's API requests through `NODE_EXTRA_CA_CERTS`; Chrome uses the trusted OS CA. `ignoreHTTPSErrors` is false. No browser download is required for the default installed Chrome channel. Close test contexts on failure; preserve generated business evidence. Traces, videos, screenshots and saved authentication state are disabled. Reports/output stay private and ignored; inspect them for credentials before sharing. Configuration follows the official [Playwright configuration](https://playwright.dev/docs/test-configuration) and [isolated contexts](https://playwright.dev/docs/api/class-browsercontext) APIs.

For manual verification, record date, browser/version, identities, resource IDs, actions and results in LAB-007 evidence; do not record passwords, cookies or tokens:

1. Open separate browser profiles for Alice, Bob and Admin. Check trusted TLS, login, account roles and cookie Secure/HttpOnly/Strict flags.
2. Keep two live circuits open. Submit Alice/Bob reservations concurrently; use Reload/filter through each circuit. Lists must remain owned; exchange detail URLs for reservations, payments and notifications and expect 404. Admin may inspect both.
3. Create/edit a tour and session as Admin. Change price/capacity and provoke a stale-version conflict with two tabs. Show a price-validation error and a failed reservation dependency response; retain the original key on an uncertain retry.
4. Click diagnostics refresh and switch all four services. Click projection refresh. Confirm actual data changes; merely seeing prerendered HTML is insufficient. Inspect/replay only corrected lab messages, acknowledging visibility and duplicate risk.
5. Show charge/refund status, fake notification content, Unknown provider resolution/reconcile and ManualReview compensation retry. Follow `demo-guide.md` for controlled failures.
6. Leave an active progress page, navigate away, and inspect service request activity for at least two polling periods; its polling must stop. Return/reload and verify it resumes only for nonterminal work.
7. Keep Account/progress open and sign out in another tab. Within thirty seconds the old circuit must lose authentication; protected operations must stop. Separately leave a real session for thirty minutes to observe browser expiration (server expired-ticket/client expiration tests do not replace this check).
8. Attempt login/logout without antiforgery, external return URLs and non-admin mutations; confirm 400/401/403/404 boundaries. Stop/start the lab normally and confirm an unexpired, non-signed-out cookie survives with encrypted keys.

## LAB-008 prerequisites

Review the outstanding browser AC-26/27/28/29 evidence independently. LAB-008 is reviewable infrastructure only: verify current Free Tier/Fargate/Neon quotas and cost limits, Neon role/schema isolation and runtime/migration TLS/pooling, prefix/tag restrictions, per-service IAM queues/DLQs, external secret/key ownership, encrypted persistent Data Protection, and the single-Gateway circuit/ingress model. Direct ingress needs an explicit limited-session access decision; any introduced proxy requires its trusted-header test. Prepare one-shot migrations and session start/stop/preflight/destroy artifacts without deploying. No local result establishes AWS readiness.
