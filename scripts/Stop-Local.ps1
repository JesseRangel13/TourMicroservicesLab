$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
docker compose stop
if ($LASTEXITCODE -ne 0) { throw 'Stop failed.' }
Write-Host 'Stopped local containers. PostgreSQL and queues remain on named volumes.'
