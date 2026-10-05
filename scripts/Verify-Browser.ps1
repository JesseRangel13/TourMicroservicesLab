param([switch]$ListOnly)
$ErrorActionPreference = 'Stop'
$previousLabCa = $env:NODE_EXTRA_CA_CERTS
Push-Location (Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/EndToEnd')
try {
    $env:NODE_EXTRA_CA_CERTS = (Resolve-Path '../../.local/certificates/ca.crt').Path
    npm.cmd ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Browser dependency restore failed.' }
    if ($ListOnly) { npm.cmd run list } else { npm.cmd test }
    if ($LASTEXITCODE -ne 0) { throw 'Browser checks failed. Keep output private; do not bypass TLS or browser security.' }
} finally {
    $env:NODE_EXTRA_CA_CERTS = $previousLabCa
    Pop-Location
}
