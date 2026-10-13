#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Preflight','Start','Stop')][string]$Action,
    [Parameter(Mandatory)][string]$SessionFile,[switch]$ReadOnlyAws,[switch]$Execute,[string]$ConfirmScope)
. "$PSScriptRoot/Shared.ps1"
$session=Read-Session $SessionFile
Write-Host "$Action scope: $($session.account_id) / $($session.region) / $($session.prefix)."
$hourly=0.0
foreach($name in $ServiceNames) {
    $size=$session.task_sizes[$name]
    if(!$size -or !(($size.cpu -eq 512 -and $size.memory -in @(1024,2048)) -or ($size.cpu -eq 1024 -and $size.memory -eq 2048))){throw 'Reviewed compatible Fargate sizing for all five services required.'}
    $hourly+=($size.cpu/1024)*0.0404784+($size.memory/1024)*0.004446+0.005
}
Write-Host "Tags: Project=TourMicroservicesLab Environment=study. Configured compute + IPv4 estimate: USD $($hourly.ToString('F6',[Globalization.CultureInfo]::InvariantCulture))/hour; extras in docs/costs.md."
if($Action -eq 'Preflight') {
    foreach($tool in @('aws','terraform','dotnet','docker','curl')) {if(!(Get-Command $tool -ErrorAction SilentlyContinue)){throw "Required tool missing: $tool"}}
    foreach($name in $ServiceNames){if(!$session.images[$name].StartsWith("$($session.account_id).dkr.ecr.$($session.region).amazonaws.com/$($session.prefix)/$name@sha256:") -or $session.images[$name] -notmatch '@sha256:[0-9a-f]{64}$' -or $session.images[$name] -match '@sha256:0{64}$'){throw 'Actual scoped image digests required; example placeholders are not deployable.'}}
    if($ReadOnlyAws) {
        Assert-Identity $session; [void](Assert-OwnedServices $session)
        foreach($name in $ServiceNames) {
            $digest=($session.images[$name] -split '@')[1]
            [void](Invoke-Aws $session @('ecr','describe-images','--repository-name',"$($session.prefix)/$name",'--image-ids',"imageDigest=$digest",'--registry-id',$session.account_id))
            foreach($arn in $session.secret_arns[$name].Values) {
                $parameterName=($arn -split ':parameter')[1]
                $metadata=Invoke-Aws $session @('ssm','describe-parameters','--parameter-filters',"Key=Name,Option=Equals,Values=$parameterName")
                if($metadata.Parameters.Count -ne 1 -or $metadata.Parameters[0].Type -ne 'SecureString' -or $metadata.Parameters[0].Tier -ne 'Standard'){throw 'Expected existing standard SecureString parameters.'}
            }
        }
    }
    Write-Host 'Configuration checks passed. Also review migrations/isolation, CA trust, task sizing, quotas and costs before starting.'
    return
}
if(!$Execute){Write-Host 'Preview only: no AWS request or mutation. Add -Execute with exact confirmation in LAB-009.';return}
Assert-Identity $session; Assert-Confirmation $session $ConfirmScope $Action.ToUpperInvariant(); [void](Assert-OwnedServices $session)
if($Action -eq 'Stop') {
    Stop-OwnedServices $session;Write-Host 'Verified zero running/pending lab tasks. Database, queues, parameters and images retained.';return
}
& "$PSScriptRoot/Invoke-Session.ps1" -Action Preflight -SessionFile $SessionFile -ReadOnlyAws
try {
    foreach($name in $ServiceNames){
        $count = if($name -eq 'gateway' -and $session.alb_endpoint){2}else{1}
        [void](Invoke-Aws $session @('ecs','update-service','--cluster',$session.cluster,'--service',$session.services[$name],'--desired-count',"$count"))
    }
    [void](Invoke-Aws $session (@('ecs','wait','services-stable','--cluster',$session.cluster,'--services')+@($session.services.Values)))
    $tasks=Invoke-Aws $session @('ecs','list-tasks','--cluster',$session.cluster,'--service-name',$session.services.gateway,'--desired-status','RUNNING')
    if($tasks.taskArns.Count -lt 1){throw 'At least one Gateway task required.'}
    $task=Invoke-Aws $session (@('ecs','describe-tasks','--cluster',$session.cluster,'--tasks')+@($tasks.taskArns[0]))
    $eni=($task.tasks[0].attachments.details|Where-Object name -eq 'networkInterfaceId').value
    $network=Invoke-Aws $session @('ec2','describe-network-interfaces','--network-interface-ids',$eni)
    $ip=$network.NetworkInterfaces[0].Association.PublicIp
    if(!$ip){throw 'Gateway public IP missing.'}
    if($session.alb_endpoint){
        Write-Host "ALB Gateway URL: $($session.alb_endpoint) (balanced across 2 Gateway tasks with cookie stickiness)."
    }
    Write-Host "Direct Gateway mapping: $ip lab.tours.test ; URL https://lab.tours.test:8443. Trust the public lab CA on the PC."
    & "$PSScriptRoot/Verify-Readiness.ps1" -GatewayIp $ip
    Write-Host 'Study session ready. Stop explicitly afterward; this is not a guaranteed timed billing cutoff.'
} catch {
    Stop-OwnedServices $session
    throw 'Start/readiness failed; lab returned to zero tasks. Inspect private diagnostics and prerequisites.'
}
