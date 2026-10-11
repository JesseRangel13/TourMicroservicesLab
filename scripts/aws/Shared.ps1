#requires -Version 7.0
$RepoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ServiceNames=@('gateway','catalog','reservations','payments','notifications')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
function Read-Session([string]$Path) {
    $s=Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -AsHashtable
    if($s.prefix -notmatch '^tourlab-[a-z0-9-]{2,20}$' -or $s.region -ne 'us-east-1' -or $s.account_id -notmatch '^\d{12}$' -or $s.cluster -ne $s.prefix){throw 'Invalid study scope.'}
    if((($s.services.Keys|Sort-Object) -join ',') -ne (($ServiceNames|Sort-Object) -join ',')){throw 'Exactly five owned services required.'}
    foreach($name in $ServiceNames){if($s.services[$name] -ne "$($s.prefix)-$name"){throw 'Service outside study scope.'}}
    foreach($name in $ServiceNames) {
        $required=@('LAB_CA_PEM','LAB_TLS_CERT_PEM','LAB_TLS_KEY_PEM','LAB_JWT_PUBLIC_PEM','ConnectionStrings__Runtime')
        if($name -eq 'gateway'){$required+=@('LAB_JWT_PRIVATE_PEM','LAB_PROTECTION_CERT_PEM','LAB_PROTECTION_KEY_PEM')}
        if($name -eq 'catalog'){$required+='LAB_SERVICE_PUBLIC_PEM'};if($name -eq 'reservations'){$required+='LAB_SERVICE_PRIVATE_PEM'}
        if((($s.secret_arns[$name].Keys|Sort-Object) -join ',') -ne (($required|Sort-Object) -join ',')){throw 'Invalid per-service secret boundary.'}
        foreach($entry in $s.secret_arns[$name].GetEnumerator()){if($entry.Value -cne "arn:aws:ssm:$($s.region):$($s.account_id):parameter/$($s.prefix)/$name/$($entry.Key)"){throw 'Secret reference outside exact account/service/prefix scope.'}}
    }
    return $s
}
function Invoke-Aws($Session,[string[]]$Arguments) {
    $argsList=@($Arguments)+@('--region',$Session.region,'--output','json','--no-cli-pager','--cli-connect-timeout','5','--cli-read-timeout','10')
    if($Session.profile){$argsList+=@('--profile',$Session.profile)}
    $log=Join-Path $RepoRoot '.local/aws-command-error.log'
    $previousAttempts=$env:AWS_MAX_ATTEMPTS
    try {
        $env:AWS_MAX_ATTEMPTS='1'
        $output=& aws @argsList 2>$log
        if($LASTEXITCODE -ne 0){throw 'AWS operation failed. Inspect private diagnostics; no automatic retries of mutations.'}
    } finally { $env:AWS_MAX_ATTEMPTS=$previousAttempts }
    if($output){return ($output -join "`n"|ConvertFrom-Json -AsHashtable)}
}
function Assert-Identity($Session) {
    $identity=Invoke-Aws $Session @('sts','get-caller-identity')
    if($identity.Account -ne $Session.account_id -or $identity.Arn -match ':root$'){throw 'Wrong account or root credentials; use the study profile/SSO.'}
}
function Assert-Confirmation($Session,[string]$Confirmation,[string]$Action) {
    if($Confirmation -cne "$Action $($Session.prefix) $($Session.account_id)"){throw 'Explicit action, prefix and account confirmation required.'}
}
function Assert-OwnedServices($Session) {
    $cluster=Invoke-Aws $Session @('ecs','describe-clusters','--clusters',$Session.cluster,'--include','TAGS')
    $clusterTags=@{};foreach($tag in $cluster.clusters[0].tags){$clusterTags[$tag.key]=$tag.value}
    if($cluster.failures.Count -or $cluster.clusters.Count -ne 1 -or $clusterTags.Project -ne 'TourMicroservicesLab' -or $clusterTags.Environment -ne 'study' -or $clusterTags.Prefix -ne $Session.prefix){throw 'Cluster ownership tags mismatch.'}
    $result=Invoke-Aws $Session (@('ecs','describe-services','--cluster',$Session.cluster,'--services')+@($Session.services.Values)+@('--include','TAGS'))
    if($result.failures.Count -or $result.services.Count -ne 5){throw 'Missing owned ECS services.'}
    foreach($service in $result.services) {
        $tags=@{};foreach($tag in $service.tags){$tags[$tag.key]=$tag.value}
        if($tags.Project -ne 'TourMicroservicesLab' -or $tags.Environment -ne 'study' -or $tags.Prefix -ne $Session.prefix -or $service.serviceName -notin $Session.services.Values){throw 'Refusing untagged or unrelated services.'}
    }
    return $result.services
}
function Wait-Zero($Session) {
    for($i=0;$i -lt 150;$i++) {
        $services=Assert-OwnedServices $Session
        $tasks=Invoke-Aws $Session @('ecs','list-tasks','--cluster',$Session.cluster,'--desired-status','RUNNING')
        if(!@($services|Where-Object {$_.desiredCount -ne 0 -or $_.runningCount -ne 0 -or $_.pendingCount -ne 0}).Count -and !$tasks.taskArns.Count){return}
        Start-Sleep -Seconds 2
    }
    throw 'Lab tasks remain. Stop is not verified; inspect ECS and retry explicitly.'
}

function Stop-OwnedServices($Session) {
    $failed=$false
    foreach($name in $ServiceNames) {
        try { [void](Invoke-Aws $Session @('ecs','update-service','--cluster',$Session.cluster,'--service',$Session.services[$name],'--desired-count','0')) }
        catch { $failed=$true; Write-Warning "Stop request failed for $name; continuing with other owned services." }
    }
    if($failed){throw 'Some stop requests failed. Zero tasks is NOT verified; retry stop explicitly and inspect ECS.'}
    Wait-Zero $Session
}

function Assert-DestroyPlan($Session,$Plan,$Resources) {
    foreach($name in @('prefix','account_id','region')) {
        if($Plan.variables[$name].value -cne $Session[$name]){throw 'Plan belongs to a different study scope.'}
    }
    $managed=@($Resources|Where-Object {$_.mode -eq 'managed'})
    $addresses=@($managed|ForEach-Object {$_['address']})
    $deletions=@($Plan.resource_changes|Where-Object {$_.mode -eq 'managed' -and ($_.change.actions -join ',') -ne 'no-op'})
    if(!$deletions.Count){throw 'Plan contains no destruction.'}
    foreach($change in $deletions) {
        if(($change.change.actions -join ',') -cne 'delete' -or $change.address -notin $addresses){throw 'Plan contains an unknown resource or non-delete action.'}
        $before=$change.change.before
        $owned=@($managed|Where-Object {$_.address -ceq $change.address})
        if($owned.Count -ne 1 -or $before.id -cne $owned[0].values.id){throw 'Plan does not match current owned state.'}
        if($before.tags_all -and ($before.tags_all.Project -ne 'TourMicroservicesLab' -or $before.tags_all.Environment -ne 'study' -or $before.tags_all.Prefix -ne $Session.prefix)){throw 'Refreshed plan ownership tags mismatch.'}
    }
}
