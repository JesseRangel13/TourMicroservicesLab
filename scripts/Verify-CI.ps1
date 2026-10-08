param([Parameter(Mandatory)][ValidateSet('Unit', 'Live', 'Controlled', 'Restart', 'RateLimit')][string]$Suite)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-Location (Split-Path $PSScriptRoot -Parent)
$privateResults = Join-Path (Get-Location) 'TestResults/ci/private'
New-Item -ItemType Directory -Force $privateResults | Out-Null
$names = @('LAB_TEST_ROOT', 'LAB_MESSAGING_TEST', 'LAB_PAYMENT_TEST', 'LAB_RESTART_TEST', 'LAB_RATE_LIMIT_TEST', 'LAB_MESSAGING_ENABLED', 'LAB_WORK_ENABLED')
$previous = @{}
foreach ($name in $names) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}
$paused = $Suite -in @('Controlled', 'Restart')
$filters = @{
    Unit = 'FullyQualifiedName~RuntimeOptionsTests|FullyQualifiedName~JwtValidationTests|FullyQualifiedName~CatalogContractTests|FullyQualifiedName~MessagingContractTests|FullyQualifiedName~QuoteResilienceTests|FullyQualifiedName~CircuitIsolationTests'
    Controlled = 'FullyQualifiedName~MessagingTests|FullyQualifiedName~PaymentWorkflowTests|FullyQualifiedName~SagaWorkflowTests|FullyQualifiedName~PoisonFiveReceivesCorrection|FullyQualifiedName~ProjectionIsAbsent|FullyQualifiedName~ExpiredInspections|FullyQualifiedName~PaymentPauseConfiguration'
    Restart = 'FullyQualifiedName~PersistenceTests'
    RateLimit = 'FullyQualifiedName~CredentialLimiterRejectsChangingUntrustedForwardedAddresses'
}
try {
    if ($Suite -ne 'Unit') {
        if (!(Test-Path .local/provisioner.json)) { throw 'Start the isolated lab before integration checks.' }
        $env:LAB_TEST_ROOT = (Get-Location).Path
    }
    if ($paused) {
        $env:LAB_MESSAGING_ENABLED = 'false'; $env:LAB_WORK_ENABLED = 'false'
        if ($Suite -eq 'Controlled') { $env:LAB_MESSAGING_TEST = '1'; $env:LAB_PAYMENT_TEST = '1' }
        else { $env:LAB_RESTART_TEST = '1' }
        docker compose up -d --wait --wait-timeout 180 catalog reservations payments notifications *> "$privateResults/worker-pause.log"
        if ($LASTEXITCODE -ne 0) { throw 'Controlled worker pause failed.' }
    }
    if ($Suite -eq 'RateLimit') { $env:LAB_RATE_LIMIT_TEST = '1' }
    $arguments = @('test', 'tests/Lab.Tests/Lab.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--results-directory', $privateResults,
        '--logger', "trx;LogFileName=$($Suite.ToLowerInvariant()).trx")
    if ($filters.ContainsKey($Suite)) { $arguments += @('--filter', $filters[$Suite]) }
    # Assertion messages, TRX output and console diagnostics may contain sensitive values.
    & dotnet @arguments *> "$privateResults/$($Suite.ToLowerInvariant()).log"
    $testExit = $LASTEXITCODE
    $trxPath = Join-Path $privateResults "$($Suite.ToLowerInvariant()).trx"
    if (!(Test-Path $trxPath)) { throw "$Suite produced no test report; check restore/build and reproduce locally." }
    [xml]$trx = Get-Content -Raw $trxPath
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if (!$counters -or [int]$counters.executed -eq 0) { throw "$Suite executed no tests." }
    # VSTest's Counters.notExecuted may be zero even when xUnit records skipped results.
    $skipped = @($trx.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='NotExecuted']")).Count
    if ($Suite -ne 'Live' -and $skipped -ne 0) { throw "$Suite unexpectedly skipped tests." }
    Write-Host "$Suite`: $($counters.passed) passed, $($counters.failed) failed, $skipped skipped."
    if ($testExit -ne 0 -or [int]$counters.failed -gt 0) { throw "$Suite failed. See the safe test report for test names and reproduce locally for details." }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    & "$PSScriptRoot/Write-CIReports.ps1"
    if ($paused) {
        docker compose up -d --wait --wait-timeout 180 catalog reservations payments notifications *> "$privateResults/worker-resume.log"
        if ($LASTEXITCODE -ne 0) { throw 'Hosted workers could not be restored.' }
    }
}
