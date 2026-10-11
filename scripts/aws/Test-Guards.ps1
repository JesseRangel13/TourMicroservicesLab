#requires -Version 7.0
param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Shared.ps1"
$session=Read-Session (Join-Path $RepoRoot 'config/aws.session.example.json')
$passed=0
function Reject([scriptblock]$Check) {
    $rejected=$false
    try { & $Check } catch { $rejected=$true }
    if(!$rejected){throw 'Expected unsafe input to be rejected.'}
    $script:passed++
}
$fixture=Join-Path $RepoRoot '.local/lab008-invalid-session.json'
try {
    $invalid=Get-Content (Join-Path $RepoRoot 'config/aws.session.example.json') -Raw|ConvertFrom-Json -AsHashtable
    $invalid.services.catalog='unrelated-catalog'
    $invalid|ConvertTo-Json -Depth 12|Set-Content $fixture
    Reject { Read-Session $fixture|Out-Null }
    $invalid.services.catalog=$session.services.catalog
    $invalid.secret_arns.catalog.LAB_TLS_KEY_PEM=$session.secret_arns.gateway.LAB_TLS_KEY_PEM
    $invalid|ConvertTo-Json -Depth 12|Set-Content $fixture
    Reject { Read-Session $fixture|Out-Null }
    Reject { Assert-Confirmation $session 'STOP other 111111111111' 'STOP' }
    # No AWS executable is used: fail any accidental request from preview paths.
    function aws { throw 'Preview attempted AWS access.' }
    foreach($action in @('Start','Stop')) {
        & "$PSScriptRoot/Invoke-Session.ps1" -Action $action -SessionFile (Join-Path $RepoRoot 'config/aws.session.example.json')
        $passed++
    }
    & "$PSScriptRoot/Review-Destroy.ps1" -SessionFile (Join-Path $RepoRoot 'config/aws.session.example.json') -VarsFile (Join-Path $RepoRoot 'infra/terraform/example.tfvars.json')
    $passed++
    $resource=@{mode='managed';address='aws_ecs_cluster.lab';values=@{id='owned'}}
    $plan=@{variables=@{prefix=@{value=$session.prefix};account_id=@{value=$session.account_id};region=@{value=$session.region}};resource_changes=@(@{mode='managed';address=$resource.address;change=@{actions=@('delete');before=@{id='owned';tags_all=@{Project='TourMicroservicesLab';Environment='study';Prefix=$session.prefix}}}})}
    Assert-DestroyPlan $session $plan @($resource);$passed++
    $plan.resource_changes[0].change.actions=@('create')
    Reject { Assert-DestroyPlan $session $plan @($resource) }
    $plan.resource_changes[0].change.actions=@('delete')
    $plan.resource_changes[0].change.before.tags_all.Prefix='tourlab-other'
    Reject { Assert-DestroyPlan $session $plan @($resource) }
    $plan.variables.account_id.value='222222222222'
    Reject { Assert-DestroyPlan $session $plan @($resource) }
    $script:updates=0;$script:wrongTags=$false
    function Invoke-Aws($Session,[string[]]$Arguments) {
        switch($Arguments[1]) {
            'update-service' { $script:updates++;return @{} }
            'describe-clusters' { return @{failures=@();clusters=@(@{tags=@(@{key='Project';value='TourMicroservicesLab'},@{key='Environment';value='study'},@{key='Prefix';value=$Session.prefix})})} }
            'describe-services' {
                $tagPrefix=if($script:wrongTags){'tourlab-other'}else{$Session.prefix}
                return @{failures=@();services=@($ServiceNames|ForEach-Object {@{serviceName=$Session.services[$_];desiredCount=0;runningCount=0;pendingCount=0;tags=@(@{key='Project';value='TourMicroservicesLab'},@{key='Environment';value='study'},@{key='Prefix';value=$tagPrefix})}})}
            }
            'list-tasks' { return @{taskArns=@()} }
            default { throw 'Unexpected mocked API.' }
        }
    }
    [void](Assert-OwnedServices $session);Stop-OwnedServices $session
    if($updates -ne 5){throw 'Stop must address all five owned services.'};$passed++
    $script:wrongTags=$true
    Reject { Assert-OwnedServices $session|Out-Null }
    Write-Host "$passed offline guard checks passed; no AWS requests."
} finally {
    if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture}
}
