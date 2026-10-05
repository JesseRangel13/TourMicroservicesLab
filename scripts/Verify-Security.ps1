param([switch]$RateLimitOnly)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$env:LAB_TEST_ROOT = (Get-Location).Path
try {
    if ($RateLimitOnly) {
        $env:LAB_RATE_LIMIT_TEST = '1'
        dotnet test --no-build --filter 'FullyQualifiedName~CredentialLimiterRejectsChangingUntrustedForwardedAddresses' --logger 'trx;LogFileName=lab007-rate-limit.trx'
    } else {
        dotnet test --no-build --filter 'FullyQualifiedName~SecurityTests|FullyQualifiedName~CircuitIsolationTests|FullyQualifiedName~JwtValidationTests|FullyQualifiedName~IntegrationTests|FullyQualifiedName~CatalogHttpTests' --logger 'trx;LogFileName=lab007-security.trx'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Security checks failed.' }
} finally {
    Remove-Item Env:LAB_TEST_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:LAB_RATE_LIMIT_TEST -ErrorAction SilentlyContinue
}
