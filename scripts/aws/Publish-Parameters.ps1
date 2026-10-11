#requires -Version 7.0
param([Parameter(Mandatory)][string]$SessionFile, [switch]$Execute, [switch]$Overwrite, [string]$ConfirmScope)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Shared.ps1"
$session=Read-Session $SessionFile
$privateRoot=Join-Path $RepoRoot '.local/aws'
& dotnet run --project (Join-Path $RepoRoot 'tools/Lab.Provisioner') -c Release --no-build -- validate-external (Join-Path $privateRoot 'provisioner.external.json')
if($LASTEXITCODE -ne 0){throw 'External configuration validation failed before any parameter publication.'}
$settings=Get-Content (Join-Path $privateRoot 'provisioner.external.json') -Raw | ConvertFrom-Json -AsHashtable
$certs=Join-Path $privateRoot 'certificates'
$files=@{LAB_CA_PEM='ca.crt';LAB_JWT_PUBLIC_PEM='jwt.pub';LAB_JWT_PRIVATE_PEM='jwt.key';LAB_SERVICE_PUBLIC_PEM='reservations-signing.pub';LAB_SERVICE_PRIVATE_PEM='reservations-signing.key';LAB_PROTECTION_CERT_PEM='protection.crt';LAB_PROTECTION_KEY_PEM='protection.key'}
if ($Execute) { Assert-Identity $session; Assert-Confirmation $session $ConfirmScope 'PARAMETERS' }
foreach($service in $ServiceNames) {
    foreach($entry in $session.secret_arns[$service].GetEnumerator()) {
        $value= switch($entry.Key) {
            'ConnectionStrings__Runtime' { $schema=if($service -eq 'gateway'){'identity'}else{$service}; $settings.RuntimeConnections[$schema] }
            'LAB_TLS_CERT_PEM' { Get-Content (Join-Path $certs "$service.crt") -Raw }
            'LAB_TLS_KEY_PEM' { Get-Content (Join-Path $certs "$service.key") -Raw }
            default { if(!$files.ContainsKey($entry.Key)){throw 'Unexpected secret key.'}; Get-Content (Join-Path $certs $files[$entry.Key]) -Raw }
        }
        if ([Text.Encoding]::UTF8.GetByteCount($value) -gt 4096 -or !$value) {throw 'Empty/oversized standard parameter; never silently upgrade tier.'}
        $name=($entry.Value -split ':parameter')[1]
        $request=@{Name=$name;Value=$value;Type='SecureString';Tier='Standard';KeyId='alias/aws/ssm';Overwrite=[bool]$Overwrite}
        if(!$Overwrite){$request.Tags=@(@{Key='Project';Value='TourMicroservicesLab'},@{Key='Environment';Value='study'},@{Key='Prefix';Value=$session.prefix})}
        $requestPath=Join-Path $privateRoot 'parameter-request.json'
        $request|ConvertTo-Json -Depth 6|Set-Content $requestPath
        try {
            if($Execute){
                if($Overwrite){
                    $result=Invoke-Aws $session @('ssm','list-tags-for-resource','--resource-type','Parameter','--resource-id',$name)
                    $tags=@{};foreach($tag in $result.TagList){$tags[$tag.Key]=$tag.Value}
                    if($tags.Project -ne 'TourMicroservicesLab' -or $tags.Environment -ne 'study' -or $tags.Prefix -ne $session.prefix){throw 'Refusing overwrite of an unrelated or untagged parameter.'}
                }
                [void](Invoke-Aws $session @('ssm','put-parameter','--cli-input-json',"file://$requestPath"))
            }
        } finally { Remove-Item -LiteralPath $requestPath }
        Write-Host "$name : size validated; execute=$Execute"
    }
}
