#requires -Version 7.0
param([string]$SettingsFile=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local/aws/provisioner.external.json'),[switch]$Execute,[string]$ConfirmDatabase)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$Execute){Write-Host 'Preview only. Build Release, verify Neon permissions/TLS and review private settings. No database connection.';return}
if($ConfirmDatabase -cne 'MIGRATE tourlab study'){throw 'Explicit study database confirmation required.'}
Push-Location $repo
try {
    dotnet run --project tools/Lab.Provisioner -c Release --no-build --no-restore -- bootstrap-external $SettingsFile --execute-external *> .local/aws/migration.log
    if($LASTEXITCODE -ne 0){throw 'One-shot migration failed. Hosts must stay at zero; inspect private diagnostics.'}
    Write-Host 'Owned roles, ordered migrations and idempotent seeds completed. Verify runtime permission denials before starting.'
} finally {Pop-Location}
