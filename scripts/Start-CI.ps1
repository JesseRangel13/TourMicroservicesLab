# Only for a fresh disposable Linux runner. Local PC data is never regenerated.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-Location (Split-Path $PSScriptRoot -Parent)
if (!$IsLinux -or $env:GITHUB_ACTIONS -ne 'true' -or $env:COMPOSE_PROJECT_NAME -notmatch '^tourlab-ci-[0-9]+-[0-9]+$') {
    throw 'Start-CI requires a GitHub-hosted Linux job with a run-specific tourlab-ci project.'
}
if (Test-Path .local) { throw 'CI requires a fresh workspace; existing private configuration is never overwritten.' }
New-Item -ItemType Directory .local | Out-Null
chmod 700 .local
if ($LASTEXITCODE -ne 0) { throw 'Private directory permissions failed.' }

function Invoke-Private([string]$Label, [scriptblock]$Action) {
    & $Action *> .local/ci-startup.log
    if ($LASTEXITCODE -ne 0) { throw "$Label failed. Reproduce locally; raw startup output is not published." }
    Write-Host "$Label passed."
}

# Restrict new files before generating secrets. Individual read-only bind mounts must
# be readable by container User=app; the host parent remains accessible only to the runner.
Invoke-Private 'Private configuration generation' {
    bash -c 'umask 077; dotnet run --project tools/Lab.Provisioner -c Release --no-build --no-restore -- init'
}
$runtimeFiles = @(Get-ChildItem .local/*.container.json) + @(Get-ChildItem .local/certificates/* | Where-Object Name -ne 'ca.key')
foreach ($file in $runtimeFiles) {
    chmod 644 $file.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Read-only container file permissions failed.' }
}
Invoke-Private 'Trust generated public lab CA' {
    sudo install -m 644 .local/certificates/ca.crt /usr/local/share/ca-certificates/tourlab-ci.crt
    if ($LASTEXITCODE -ne 0) { throw 'CA installation failed.' }
    sudo update-ca-certificates
}
Invoke-Private 'Compose validation' { docker compose config --quiet }
Invoke-Private 'PostgreSQL startup' { docker compose up -d --wait --wait-timeout 120 postgres }
Invoke-Private 'ElasticMQ startup' { docker compose up -d elasticmq }
Invoke-Private 'Owned roles, migrations and seeds' {
    dotnet run --project tools/Lab.Provisioner -c Release --no-build --no-restore -- bootstrap
}
# QueueBootstrap uses the existing bounded SDK calls; readiness retries precede any tests.
$queuesReady = $false
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    dotnet run --project tools/Lab.Provisioner -c Release --no-build --no-restore -- queues *> .local/ci-queues.log
    if ($LASTEXITCODE -eq 0) { $queuesReady = $true; break }
    Start-Sleep -Seconds 2
}
if (!$queuesReady) { throw 'ElasticMQ queue initialization failed.' }
Invoke-Private 'Five Release images' { docker compose build gateway catalog reservations payments notifications }
Invoke-Private 'Five HTTPS application hosts' {
    docker compose up -d --wait --wait-timeout 180 gateway catalog reservations payments notifications
}
