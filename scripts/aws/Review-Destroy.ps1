#requires -Version 7.0
param([Parameter(Mandatory)][string]$SessionFile,[Parameter(Mandatory)][string]$VarsFile,
    [switch]$PreparePlan,[switch]$Execute,[switch]$AllowQueueDeletion,[switch]$AllowImageDeletion,[string]$ConfirmScope,[string]$ReviewedPlanSha256)
. "$PSScriptRoot/Shared.ps1"
$s=Read-Session $SessionFile
if(!$PreparePlan -and !$Execute){Write-Host "Preview: owned $($s.prefix) infrastructure only. No AWS requests. Use -PreparePlan for a read-only review, then explicit -Execute with consent and plan hash.";return}
Assert-Identity $s; [void](Assert-OwnedServices $s); Wait-Zero $s
if(!$AllowQueueDeletion -or !$AllowImageDeletion){throw 'Destroy removes queued work and infrastructure. Explicit queue/image deletion consent required, separately from stop.'}
$tfRoot=Join-Path $RepoRoot 'infra/terraform';$plan=Join-Path $RepoRoot '.local/aws/destroy.tfplan'
$state=& terraform "-chdir=$tfRoot" show -json
if($LASTEXITCODE -ne 0){throw 'Private Terraform state inspection failed.'}
$state=$state -join "`n"|ConvertFrom-Json -AsHashtable
if($state.values.root_module.child_modules){throw 'Unexpected modules; refuse unknown destroy scope.'}
$resources=$state.values.root_module.resources
if(!$resources.Count){throw 'No owned state to destroy.'}
foreach($resource in $resources) {
    if($resource.mode -eq 'data'){continue}
    if($resource.mode -ne 'managed' -or $resource.provider_name -ne 'registry.terraform.io/hashicorp/aws'){throw 'Unknown state resource.'}
    $v=$resource.values
    if($v.tags_all) {
        if($v.tags_all.Project -ne 'TourMicroservicesLab' -or $v.tags_all.Environment -ne 'study' -or $v.tags_all.Prefix -ne $s.prefix){throw 'Untagged/unrelated state resource; destruction refused.'}
    } else {
        $ownedIds=@($resources|Where-Object {$_.values.tags_all.Prefix -eq $s.prefix}|ForEach-Object {$_.values.id})
        switch($resource.type) {
            'aws_route' {if($v.route_table_id -notin $ownedIds){throw 'Unrelated route table.'}}
            'aws_route_table_association' {if($v.route_table_id -notin $ownedIds -or $v.subnet_id -notin $ownedIds){throw 'Unrelated association.'}}
            'aws_iam_role_policy' {if(!$v.role.StartsWith($s.prefix+'-')){throw 'Unrelated IAM role.'}}
            'aws_ecr_lifecycle_policy' {if(!$v.repository.StartsWith($s.prefix+'/')){throw 'Unrelated repository.'}}
            {$_ -in @('aws_s3_bucket_public_access_block','aws_s3_bucket_ownership_controls','aws_s3_bucket_server_side_encryption_configuration','aws_s3_bucket_policy')} {if($v.bucket -cne "$($s.prefix)-$($s.account_id)-images"){throw 'Unrelated image bucket control.'}}
            'aws_sqs_queue_redrive_allow_policy' {if(!$v.queue_url.StartsWith("https://sqs.$($s.region).amazonaws.com/$($s.account_id)/$($s.prefix)-")){throw 'Unrelated queue.'}}
            'aws_lb_listener' {if(!$v.load_balancer_arn.Contains($s.prefix)){throw 'Unrelated ALB listener.'}}
            'aws_vpc_security_group_ingress_rule' {if($v.security_group_id -notin $ownedIds){throw 'Unrelated ingress rule.'}}
            'aws_vpc_security_group_egress_rule' {if($v.security_group_id -notin $ownedIds){throw 'Unrelated egress rule.'}}
            default {throw "Resource lacks ownership tags: $($resource.type)"}
        }
    }
    Write-Host "$($resource.address) : $($v.id)"
}
if(!$Execute) {
    & terraform "-chdir=$tfRoot" plan -destroy "-var-file=$([IO.Path]::GetFullPath($VarsFile))" "-out=$plan" -input=false
    if($LASTEXITCODE -ne 0){throw 'Destroy review plan failed.'}
    $prepared=& terraform "-chdir=$tfRoot" show -json $plan
    if($LASTEXITCODE -ne 0){throw 'Cannot inspect prepared plan.'}
    Assert-DestroyPlan $s ($prepared -join "`n"|ConvertFrom-Json -AsHashtable) $resources
    Write-Host "Review this exact private plan. SHA256=$((Get-FileHash $plan -Algorithm SHA256).Hash)";return
}
Assert-Confirmation $s $ConfirmScope 'DESTROY'
if(!$ReviewedPlanSha256 -or (Get-FileHash $plan -Algorithm SHA256).Hash -cne $ReviewedPlanSha256){throw 'Exact reviewed plan hash required.'}
$reviewed=& terraform "-chdir=$tfRoot" show -json $plan
if($LASTEXITCODE -ne 0){throw 'Cannot inspect reviewed plan.'}
Assert-DestroyPlan $s ($reviewed -join "`n"|ConvertFrom-Json -AsHashtable) $resources
& terraform "-chdir=$tfRoot" apply -input=false $plan
if($LASTEXITCODE -ne 0){throw 'Reviewed destruction failed; inspect private state.'}
Write-Host 'Owned infrastructure removed. External Neon DB and separately provisioned SSM parameters are outside this state and retained.'
