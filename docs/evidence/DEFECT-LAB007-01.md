# DEFECT-LAB007-01 — resolved

Date: October 4, 2026. Existing local repository; no cloud resources involved.

## Changes

`src/Gateway.Web/Components/Pages/CatalogAdmin.razor` preserves prerendering but disables the entire creation fieldset until its first interactive `OnAfterRender`. A visible “Catalog form connected.” status signals that the live circuit rendered the form. The fieldset and Create button also disable while saving. Prerendered controls cannot accept input or submit prematurely.

`tests/EndToEnd/security.spec.js` waits for that status, fills each field, explicitly dispatches `change` and blurs, then waits for an enabled Create button and clicks once. No retry of a non-idempotent creation action was introduced. The same run exposed two later test assumptions: the wrapped Service label contains option text, and retained projections are paginated by session UUID. The test now uses the named combobox and searches up to 100 projection pages, waiting for actual row replacement between pages. No fixture/business data was removed.

## Actual verification

- `dotnet build --no-restore`: passed, zero warnings/errors. Private output: `.local/lab007-defect01-build.log`.
- Gateway image rebuilt and Gateway recreated healthy using existing Compose infrastructure. Private output: `.local/lab007-defect01-image.log`.
- `powershell -File scripts/Verify-Browser.ps1`: **2 passed, zero failed, 45.4 seconds**. Private output: `.local/lab007-defect01-browser-paging.log`. Installed Chrome, real PostgreSQL/ElasticMQ and service APIs, trusted lab CA; TLS validation remained enabled. npm audit reported zero vulnerabilities.
- The first passing browser test exercised Admin tour/session creation, Alice/Bob reservation confirmation in independent circuits, own-list isolation and foreign-resource 404s, Admin detail, diagnostics service selection, projection pagination, Alice cancellation and Bob's unchanged confirmed reservation.
- The second passing browser test exercised real login, Secure/HttpOnly/Strict cookie flags, forged login/logout rejection and logout invalidation of an already-open circuit within the revalidation window.

Earlier follow-up runs were **1 passed / 1 failed**: first at the Service selector, then at the page-one projection assumption. They are retained privately in `.local/lab007-defect01-browser.log` and `.local/lab007-defect01-browser-final.log`. These are historical failures, not suppressed or relabeled successes. The independent `LAB-007-review.md` remains unchanged as the review of the earlier implementation.

## Limits

This resolves the reported Catalog hydration defect and verifies the two focused tests. It does not prove every failure/recovery UI, browser-observed thirty-minute expiration, measured cancellation on browser navigation, every shutdown interleaving, or AWS behavior. Generated evidence and fictional durable payment/email effects remain stored. No deployment, paid services, production accounts, TLS bypass, queue purge or volume deletion occurred. LAB-008 remains the next planned task, subject to the remaining review/acceptance work.
