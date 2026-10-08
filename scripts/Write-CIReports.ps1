param(
    [string]$InputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults/ci/private'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults/ci/public')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$summaries = @()
$failureNotes = @()
$markdown = @('# CI test results', '', 'Only code identifiers, outcomes and durations are published. Assertion messages, parameters, stack traces, machine paths and stdout are omitted.', '', '| Suite | Passed | Failed | Skipped |', '|---|---:|---:|---:|')
foreach ($file in @(Get-ChildItem -LiteralPath $InputDirectory -Filter '*.trx' -ErrorAction SilentlyContinue)) {
    # Suite names are controlled by Verify-CI; never export arbitrary input filenames.
    if ($file.BaseName -notin @('unit', 'live', 'controlled', 'restart', 'ratelimit')) { continue }
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $reader = [System.Xml.XmlReader]::Create($file.FullName, $settings)
    $raw = [System.Xml.XmlDocument]::new()
    try { $raw.Load($reader) } finally { $reader.Dispose() }
    $definitions = @{}
    foreach ($definition in $raw.SelectNodes("//*[local-name()='UnitTest']")) {
        $method = $definition.SelectSingleNode("*[local-name()='TestMethod']")
        $identifier = "$($method.className).$($method.name)"
        # Never use result.testName: theory display names can contain request bodies or secrets.
        $definitions[$definition.id] = if ($identifier -match '^Lab\.Tests\.[A-Za-z0-9_.+]+$') { $identifier } else { 'UnrecognizedTest' }
    }
    $rows = @()
    foreach ($result in $raw.SelectNodes("//*[local-name()='UnitTestResult']")) {
        $outcome = if ($result.outcome -in @('Passed', 'Failed', 'NotExecuted')) { $result.outcome } else { 'Failed' }
        $duration = [TimeSpan]::Zero
        [void][TimeSpan]::TryParse($result.duration, [Globalization.CultureInfo]::InvariantCulture, [ref]$duration)
        $name = if ($definitions.ContainsKey($result.testId)) { $definitions[$result.testId] } else { 'UnrecognizedTest' }
        $category = ''; $expectedStatus = ''; $actualStatus = ''
        if ($outcome -eq 'Failed') {
            $message = $result.SelectSingleNode(".//*[local-name()='ErrorInfo']/*[local-name()='Message']")
            if ($message -and $message.InnerText -match '^Assert\.([A-Za-z]+)\(\) Failure') { $category = "Assert.$($Matches[1])" }
            # Status enum names are an explicit finite allowlist, never arbitrary assertion values.
            foreach ($field in @('Expected', 'Actual')) {
                if ($message -and $message.InnerText -match "(?m)^$field`: +([A-Za-z]+)\s*$") {
                    $status = $Matches[1]
                    if ($status -cin [Enum]::GetNames([System.Net.HttpStatusCode])) {
                        if ($field -eq 'Expected') { $expectedStatus = $status } else { $actualStatus = $status }
                    }
                }
            }
        }
        $rows += [pscustomobject]@{ Test = $name; Outcome = $outcome; Seconds = [Math]::Max(0, $duration.TotalSeconds);
            FailureCategory = $category; ExpectedHttpStatus = $expectedStatus; ActualHttpStatus = $actualStatus }
    }
    $passed = @($rows | Where-Object Outcome -eq 'Passed').Count
    $failed = @($rows | Where-Object Outcome -eq 'Failed').Count
    $skipped = @($rows | Where-Object Outcome -eq 'NotExecuted').Count
    $suite = $file.BaseName
    $summaries += [pscustomobject]@{ Suite = $suite; Passed = $passed; Failed = $failed; Skipped = $skipped; Tests = $rows }
    $markdown += "| $suite | $passed | $failed | $skipped |"
    foreach ($failure in $rows | Where-Object Outcome -eq 'Failed') {
        $failureNotes += "Failed: ``$($failure.Test)`` ($($failure.FailureCategory); expected HTTP $($failure.ExpectedHttpStatus), actual HTTP $($failure.ActualHttpStatus)). Reproduce locally for private diagnostics."
    }

    $junit = [System.Xml.XmlDocument]::new()
    $root = $junit.CreateElement('testsuite')
    $root.SetAttribute('name', $suite); $root.SetAttribute('tests', [string]$rows.Count)
    $root.SetAttribute('failures', [string]$failed); $root.SetAttribute('skipped', [string]$skipped)
    [void]$junit.AppendChild($root)
    $index = 0
    foreach ($row in $rows) {
        $case = $junit.CreateElement('testcase')
        $case.SetAttribute('name', "$($row.Test) [$index]"); $index++
        $case.SetAttribute('classname', $suite)
        $case.SetAttribute('time', $row.Seconds.ToString('0.######', [Globalization.CultureInfo]::InvariantCulture))
        if ($row.Outcome -eq 'Failed') {
            $failure = $junit.CreateElement('failure')
            $failure.SetAttribute('message', 'Test failed; reproduce locally for private diagnostics.')
            [void]$case.AppendChild($failure)
        } elseif ($row.Outcome -eq 'NotExecuted') { [void]$case.AppendChild($junit.CreateElement('skipped')) }
        [void]$root.AppendChild($case)
    }
    $junit.Save((Join-Path $OutputDirectory "$suite.xml"))
}
if ($summaries.Count -eq 0) { $markdown += ''; $markdown += 'No test report was produced. Inspect the failed restore/build/provisioning step; this is not a passing test result.' }
$markdown += ''; $markdown += $failureNotes
ConvertTo-Json -InputObject @($summaries) -Depth 5 | Set-Content (Join-Path $OutputDirectory 'results.json')
$markdown | Set-Content (Join-Path $OutputDirectory 'summary.md')
