#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^ep-[a-z0-9.-]+\.neon\.tech$')][string]$DirectHost,
    [Parameter(Mandatory)][ValidatePattern('^ep-[a-z0-9.-]+\.neon\.tech$')][string]$RuntimeHost,
    [PSCredential]$AdminCredential
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
if (Test-Path .local/aws) { throw 'Existing cloud keys/configuration are preserved. Rotation requires a separate procedure.' }
New-Item -ItemType Directory -Force .local/aws | Out-Null
if ($IsWindows) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    icacls .local/aws /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Private ACL setup failed.' }
} else { chmod 700 .local/aws; if ($LASTEXITCODE -ne 0) { throw 'Private permissions failed.' } }
if (!$AdminCredential) { $AdminCredential = Get-Credential -Message 'Neon study database administrator; never enter credentials in command arguments.' }
dotnet run --project tools/Lab.Provisioner -c Release --no-build --no-restore -- init-cloud
if ($LASTEXITCODE -ne 0) { throw 'Cloud-only private key generation failed.' }
$settings = Get-Content .local/aws/provisioner.json -Raw | ConvertFrom-Json -AsHashtable
function Connection([string]$HostName, [string]$Role, [string]$Password) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host']=$HostName; $builder['Port']=5432; $builder['Database']='tourlab'; $builder['Username']=$Role; $builder['Password']=$Password
    $builder['SSL Mode']='VerifyFull'; $builder['Maximum Pool Size']=5; $builder['Timeout']=5; $builder['Command Timeout']=10
    return $builder.ConnectionString
}
$settings.AdminConnection = Connection $DirectHost $AdminCredential.UserName $AdminCredential.GetNetworkCredential().Password
$settings.RuntimeConnections=@{}; $settings.MigrationConnections=@{}
foreach($schema in @('catalog','reservations','payments','notifications','identity')) {
    $settings.RuntimeConnections[$schema]=Connection $RuntimeHost "${schema}_runtime" $settings.RuntimePasswords[$schema]
    $settings.MigrationConnections[$schema]=Connection $DirectHost "${schema}_migrator" $settings.MigrationPasswords[$schema]
}
$settings | ConvertTo-Json -Depth 8 | Set-Content .local/aws/provisioner.external.json
Write-Host 'Generated private cloud certificates, keys and external provisioning settings. No database or AWS connection was made.'
