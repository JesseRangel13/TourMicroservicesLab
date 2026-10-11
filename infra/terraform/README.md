# LAB-008 Terraform

Terraform 1.13.5 was used locally; AWS provider6.68.0 is locked for Windows/Linux AMD64. Requirements: Terraform>=1.13.5,<2 and AWS provider~>6.0. No AWS credentials are required for these checks, from the repository root:

```powershell
terraform -chdir=infra/terraform init -backend=false
terraform -chdir=infra/terraform fmt -check -recursive
terraform -chdir=infra/terraform validate
terraform -chdir=infra/terraform test '-var-file=example.tfvars.json'
pwsh -File scripts/aws/Test-Guards.ps1
```

The test's `command=apply` uses a mock provider exclusively: it does not provision AWS. Example account, email, CIDR and image digests are fictional and unsuitable for deployment. Negative tests reject public ingress and unmeasured small task sizes.

Default task size is0.5vCPU/1GiB. Reviewed overrides allow0.5vCPU/2GiB or1vCPU/2GiB, matching the [official Fargate CPU/memory combinations](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-cpu-memory-error.html). Larger settings require a new cost/sizing review; local cold-start timing limitations are in docs/evidence/DEFECT-LAB008-01.md.

For LAB-009, copy the example to ignored `.local/aws/study.tfvars.json`, supply the study account, current client /32, real budget email, reviewed sizes, existing parameter ARNs and all five immutable ECR image digests. `terraform init` enables the local backend at `.local/aws/terraform.tfstate`. Do not move/change the backend silently; back up the entire private state and lock files securely before any lifecycle operation. Run one operator at a time. Do not upload private state, tfvars, plans or raw show JSON to CI artifacts.

A real authenticated plan, authorization to provision, and independent review are still required. ECR repositories and tasks are in the same stack: repositories must exist before image publishing in LAB-009. Review a narrowly targeted repository-only bootstrap plan for that first step; then publish the five images, replace placeholder digests and review the complete plan. Never start tasks using placeholders. Full initial service creation has desired_count0.

Resources: one VPC, two public subnets across Availability Zones `us-east-1a` and `us-east-1b`, IGW and route table; six security groups (ALB + 5 services), repositories, log groups, execution/task roles, task definitions and ECS services; one internet-facing Application Load Balancer (ALB) with HTTP port 80 listener and HTTPS target group with cookie stickiness for 2 Gateway replicas; private Cloud Map namespace/registrations for internal business services; four input queues and four DLQs; one email budget. No secret values are Terraform inputs. The session output is a non-secret operator inventory including the ALB DNS name:

```powershell
terraform -chdir=infra/terraform output -json session > .local/aws/session.json
```

See ../../docs/aws-execution.md for the staged deployment handoff, session lifecycle and destruction limits. Do not execute deployment commands during LAB-008.
