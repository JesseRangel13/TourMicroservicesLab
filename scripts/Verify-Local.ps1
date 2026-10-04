$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
dotnet build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$env:LAB_TEST_ROOT = (Get-Location).Path
try {
    dotnet test --no-build --logger 'trx;LogFileName=lab004.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Remove-Item Env:LAB_TEST_ROOT -ErrorAction SilentlyContinue }
