param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (!(Test-Path '.local/provisioner.json')) { & "$PSScriptRoot/New-LocalConfiguration.ps1" }
dotnet run --project tools/Lab.Provisioner -- refresh
if ($LASTEXITCODE -ne 0) { throw 'Private runtime configuration refresh failed.' }
docker compose up -d --wait postgres
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL startup failed.' }
docker compose up -d elasticmq s3 dynamodb
if ($LASTEXITCODE -ne 0) { throw 'ElasticMQ startup failed.' }
dotnet run --project tools/Lab.Provisioner -- bootstrap
if ($LASTEXITCODE -ne 0) { throw 'One-shot bootstrap/migration failed. Hosts were not started.' }
dotnet run --project tools/Lab.Provisioner -- queues
if ($LASTEXITCODE -ne 0) { throw 'Local queue initialization failed; rerun after ElasticMQ is ready.' }
$storageReady = $false
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    dotnet run --project tools/Lab.Provisioner -- extensions
    if ($LASTEXITCODE -eq 0) { $storageReady = $true; break }
    Start-Sleep -Seconds 2
}
if (!$storageReady) { throw 'Local storage emulator bootstrap failed.' }
if ($SkipBuild) { docker compose up -d --wait gateway catalog reservations payments notifications }
else { docker compose up -d --build --wait gateway catalog reservations payments notifications }
if ($LASTEXITCODE -ne 0) { throw 'Application startup failed.' }
Write-Host 'Open https://localhost:8443 after running scripts/Trust-LocalCa.ps1.'
