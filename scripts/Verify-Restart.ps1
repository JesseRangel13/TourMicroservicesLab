$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$env:LAB_TEST_ROOT = (Get-Location).Path
$env:LAB_RESTART_TEST = '1'
try {
    dotnet test --filter 'FullyQualifiedName~PersistenceTests' --logger 'trx;LogFileName=lab002-restart.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Persistence verification failed.' }
} finally {
    Remove-Item Env:LAB_TEST_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:LAB_RESTART_TEST -ErrorAction SilentlyContinue
}
