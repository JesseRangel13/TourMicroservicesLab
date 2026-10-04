$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$env:LAB_TEST_ROOT = (Get-Location).Path
$env:LAB_RESTART_TEST = '1'
$env:LAB_MESSAGING_ENABLED = 'false'
$env:LAB_WORK_ENABLED = 'false'
try {
    docker compose up -d --wait catalog reservations payments notifications
    if ($LASTEXITCODE -ne 0) { throw 'Could not pause workers for a stable persistence snapshot.' }
    dotnet test --filter 'FullyQualifiedName~PersistenceTests' --logger 'trx;LogFileName=lab004-restart.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Persistence verification failed.' }
} finally {
    Remove-Item Env:LAB_TEST_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:LAB_RESTART_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:LAB_MESSAGING_ENABLED -ErrorAction SilentlyContinue
    Remove-Item Env:LAB_WORK_ENABLED -ErrorAction SilentlyContinue
    docker compose up -d --wait catalog reservations payments notifications
    if ($LASTEXITCODE -ne 0) { throw 'Could not resume hosted workers.' }
}
