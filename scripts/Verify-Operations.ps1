param([switch]$SkipLive)
$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (!$SkipLive) {
    $env:LAB_TEST_ROOT=(Get-Location).Path
    try {
        dotnet test --filter 'FullyQualifiedName~QuoteResilienceTests|FullyQualifiedName~ServiceOwnedDiagnostics|FullyQualifiedName~SlowCatalogFiveFailures|FullyQualifiedName~PauseOutboxReset' --logger 'trx;LogFileName=lab006-live.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Live operational checks failed.' }
    } finally { Remove-Item Env:LAB_TEST_ROOT -ErrorAction SilentlyContinue }
}
& "$PSScriptRoot/Verify-Sagas.ps1" -Filter 'FullyQualifiedName~PoisonFiveReceivesCorrection|FullyQualifiedName~ProjectionIsAbsent|FullyQualifiedName~ExpiredInspections|FullyQualifiedName~PaymentPauseConfiguration' -ResultFile 'lab006-controlled.trx'
