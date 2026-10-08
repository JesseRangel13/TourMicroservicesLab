# Exercise the report allowlist with a failure carrying fictional sensitive data.
$ErrorActionPreference = 'Stop'
$directory = Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults/ci/report-fixture'
$rawDirectory = Join-Path $directory 'private'
$publicDirectory = Join-Path $directory 'public'
New-Item -ItemType Directory -Force $rawDirectory | Out-Null
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions>
    <UnitTest id="one"><TestMethod className="Lab.Tests.Example" name="FailureIsReported" /></UnitTest>
    <UnitTest id="two"><TestMethod className="Lab.Tests.Example" name="SuccessIsReported" /></UnitTest>
    <UnitTest id="three"><TestMethod className="Lab.Tests.Example" name="SkipIsReported" /></UnitTest>
  </TestDefinitions>
  <Results>
    <UnitTestResult testId="one" testName="SECRET-SENTINEL password in theory" outcome="Failed" duration="00:00:01">
      <Output><StdOut>SECRET-SENTINEL token</StdOut><ErrorInfo><Message>SECRET-SENTINEL connection string</Message><StackTrace>SECRET-SENTINEL private path</StackTrace></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testId="two" outcome="Passed" duration="00:00:02" />
    <UnitTestResult testId="three" outcome="NotExecuted" />
  </Results>
</TestRun>
'@ | Set-Content (Join-Path $rawDirectory 'unit.trx')
& "$PSScriptRoot/Write-CIReports.ps1" -InputDirectory $rawDirectory -OutputDirectory $publicDirectory
$published = (Get-ChildItem $publicDirectory -File | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
if ($published.Contains('SECRET-SENTINEL')) { throw 'Sensitive fixture data escaped into reports.' }
$json = Get-Content -Raw (Join-Path $publicDirectory 'results.json') | ConvertFrom-Json
if ($json[0].Passed -ne 1 -or $json[0].Failed -ne 1 -or $json[0].Skipped -ne 1) { throw 'Report counters are incorrect.' }
[xml]$xml = Get-Content -Raw (Join-Path $publicDirectory 'unit.xml')
if ($xml.testsuite.testcase.Count -ne 3 -or !$xml.SelectSingleNode('//failure') -or !$xml.SelectSingleNode('//skipped')) { throw 'JUnit results are incomplete.' }
Write-Host 'Safe report failure/pass/skip and redaction checks passed.'
