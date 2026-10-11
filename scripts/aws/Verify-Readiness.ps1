#requires -Version 7.0
param([Parameter(Mandatory)][ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')][string]$GatewayIp)
$ErrorActionPreference='Stop'
$root=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local/aws'
$settings=Get-Content (Join-Path $root 'provisioner.external.json') -Raw|ConvertFrom-Json
$admin=$settings.Users|Where-Object Name -eq 'Admin'
$bodyFile=Join-Path $root 'readiness-body.json';$replyFile=Join-Path $root 'readiness-response.json';$headerFile=Join-Path $root 'readiness-headers.txt'
$ca=Join-Path $root 'certificates/ca.crt'
function Request([string]$Path,[string[]]$Extra=@()) {
    $arguments=@('--silent','--show-error','--fail','--connect-timeout','5','--max-time','15','--cacert',$ca,'--resolve',"lab.tours.test:8443:$GatewayIp",'--output',$replyFile)
    if($IsWindows){$arguments+='--ssl-revoke-best-effort'}
    & curl @arguments @Extra "https://lab.tours.test:8443/$Path" 2>(Join-Path $root 'readiness-error.log')
    if($LASTEXITCODE -ne 0){throw 'Verified TLS readiness request failed.'}
}
try {
    @{username=$admin.Name;password=$admin.Password}|ConvertTo-Json|Set-Content $bodyFile
    Request 'auth/token' @('--header','Content-Type: application/json','--data-binary',"@$bodyFile")
    $token=(Get-Content $replyFile -Raw|ConvertFrom-Json).accessToken
    if(!$token){throw 'No verified Admin token received.'}
    "Authorization: Bearer $token"|Set-Content $headerFile
    foreach($path in @('health/ready','api/catalog/v1/tours','api/reservations/v1/reservations','api/payments/v1/payments','api/notifications/v1/notifications')) {Request $path @('--header',"@$headerFile")}
    Write-Host 'Gateway DB readiness and four owned API database queries passed over verified TLS.'
} finally {foreach($path in @($bodyFile,$replyFile,$headerFile)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}}
