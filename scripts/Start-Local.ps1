param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (!(Test-Path '.local/provisioner.json')) { & "$PSScriptRoot/New-LocalConfiguration.ps1" }
docker compose up -d --wait postgres
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL startup failed.' }
docker compose up -d elasticmq
if ($LASTEXITCODE -ne 0) { throw 'ElasticMQ startup failed.' }
dotnet run --project tools/Lab.Provisioner -- bootstrap
if ($LASTEXITCODE -ne 0) { throw 'One-shot bootstrap/migration failed. Hosts were not started.' }
if ($SkipBuild) { docker compose up -d --wait gateway catalog reservations payments notifications }
else { docker compose up -d --build --wait gateway catalog reservations payments notifications }
if ($LASTEXITCODE -ne 0) { throw 'Application startup failed.' }
Write-Host 'Open https://localhost:8443 after running scripts/Trust-LocalCa.ps1.'
