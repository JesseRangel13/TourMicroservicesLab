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
    Restart = 'FullyQualifiedName~PersistenceTests'
    RateLimit = 'FullyQualifiedName~CredentialLimiterRejectsChangingUntrustedForwardedAddresses'
}
function Invoke-TestGroup([string]$Name, [string]$Filter) {
    $arguments = @('test', 'tests/Lab.Tests/Lab.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--results-directory', $privateResults,
        '--logger', "trx;LogFileName=$($Name).trx")
    if ($Filter) { $arguments += @('--filter', $Filter) }
    # Assertion messages, TRX output and console diagnostics may contain sensitive values.
    & dotnet @arguments *> "$privateResults/$($Name).log"
    $testExit = $LASTEXITCODE
    $trxPath = Join-Path $privateResults "$($Name).trx"
    if (!(Test-Path $trxPath)) { throw "$Name produced no test report; check restore/build and reproduce locally." }
    [xml]$trx = Get-Content -Raw $trxPath
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if (!$counters -or [int]$counters.executed -eq 0) { throw "$Name executed no tests." }
    # VSTest's Counters.notExecuted may be zero even when xUnit records skipped results.
    $skipped = @($trx.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='NotExecuted']")).Count
    if ($Name -ne 'live' -and $skipped -ne 0) { throw "$Name unexpectedly skipped tests." }
    Write-Host "$Name`: $($counters.passed) passed, $($counters.failed) failed, $skipped skipped."
    return ($testExit -eq 0 -and [int]$counters.failed -eq 0)
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
        if ($Suite -eq 'Restart') {
            docker compose up -d --wait --wait-timeout 180 catalog reservations payments notifications *> "$privateResults/worker-pause.log"
            if ($LASTEXITCODE -ne 0) { throw 'Controlled worker pause failed.' }
        }
    }
    if ($Suite -eq 'RateLimit') { $env:LAB_RATE_LIMIT_TEST = '1' }
    if ($Suite -eq 'Controlled') {
        # Dedicated suites match the existing verification scripts. Each receives fresh
        # per-process limiter/breaker state, while all PostgreSQL/queue data is preserved.
        $groups = [ordered]@{
            'controlled-messaging' = 'FullyQualifiedName~MessagingTests'
            'controlled-payments' = 'FullyQualifiedName~PaymentWorkflowTests'
            'controlled-sagas' = 'FullyQualifiedName~SagaWorkflowTests'
            'controlled-operations' = 'FullyQualifiedName~PoisonFiveReceivesCorrection|FullyQualifiedName~ProjectionIsAbsent|FullyQualifiedName~ExpiredInspections|FullyQualifiedName~PaymentPauseConfiguration'
        }
        $allPassed = $true
        foreach ($group in $groups.GetEnumerator()) {
            docker compose up -d --force-recreate --wait --wait-timeout 180 catalog reservations payments notifications *> "$privateResults/worker-pause.log"
            if ($LASTEXITCODE -ne 0) { throw 'Controlled group isolation failed.' }
            if (!(Invoke-TestGroup $group.Key $group.Value)) { $allPassed = $false }
            & "$PSScriptRoot/Write-CIReports.ps1"
        }
        if (!$allPassed) { throw 'Controlled tests failed. See safe reports and reproduce locally for private details.' }
    } elseif (!(Invoke-TestGroup $Suite.ToLowerInvariant() $filters[$Suite])) {
        throw "$Suite failed. See safe reports and reproduce locally for private details."
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    & "$PSScriptRoot/Write-CIReports.ps1"
    if ($paused) {
        docker compose up -d --wait --wait-timeout 180 catalog reservations payments notifications *> "$privateResults/worker-resume.log"
        if ($LASTEXITCODE -ne 0) { throw 'Hosted workers could not be restored.' }
    }
}
