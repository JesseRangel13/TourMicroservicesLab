# 06 — AWS Fargate, Free PostgreSQL, and Costs

## Lab profile
Initial region: us-east-1. Five ECS Services, Linux x86_64, one task per service. Start at 0.25 vCPU and 0.5 GB per task ONLY if actual startup/load/memory tests support it. The Blazor Gateway may need 1 GB; increase explicitly and recalculate. Initially use on-demand Fargate so Spot interruptions do not complicate the first diagnosis.
Use a dedicated VPC with a public subnet and Internet Gateway. assignPublicIp=true permits ECR, SQS, and Neon access without NAT. A public IP does not mean an open port. Security groups: Gateway 8443 only from the student's ClientCidr; Catalog 8443 only from Gateway and Reservations; Reservations/Payments/Notifications 8443 only from Gateway. No public ingress to business services. Permit necessary egress for AWS/Neon HTTPS and internal connections. Separate SGs; do not allow inbound 0.0.0.0/0 for convenience.
Cloud Map private DNS tourlab.internal, private A records, short TTL, one name per service, fixed port 8443. Each HttpClient recycles connections to accommodate DNS changes. Private DNS is NOT a complete HTTP load balancer. No Service Connect sidecar or ALB in the initial low-cost profile.
Each service/image deploys independently; the UI is co-hosted in Gateway. Terraform manages Cloud Map, SQS, and roles. No mandatory additional remote Terraform backend; keep local state private and backed up, never in Git.

## TLS entry without buying a domain or ALB
Generate a private lab CA locally, never commit it. Gateway certificate SANs: lab.tours.test, gateway.tourlab.internal, localhost; internal certificates include each Cloud Map name. Explicitly distribute/trust the public CA certificate in clients and containers. Inject leaf private keys through secrets; keep the CA private key local only.
A script obtains the Gateway task's public IP and explains how to map lab.tours.test in the PC hosts file. URL: https://lab.tours.test:8443. The student trusts the local CA to open Blazor. Curl uses --cacert and --resolve. IP may change after restart: update mapping, do not hardcode it or disable SSL checks.
Do not claim a publicly browser-trusted certificate or a stable public endpoint. This profile serves the authorized PC. A future public demo may use ALB + ACM and an owned domain, with a separate estimate.

## Real, free PostgreSQL
Use Neon Free outside AWS, in a nearby matching region where available. One project, one database, five isolated schemas/roles. Do not provision RDS/Aurora by default. The user handles account setup and connection strings; bootstrap scripts do not print secrets. Verify plan/quotas in the console before use; never automatically enable a paid plan.
Official announcement dated October 2, 2026: Free includes 1 GB per project and 100 CU-hours/month per project. Quotas may change; older documentation still lists 0.5 GB. Keep the dataset below 100 MB, without images or bulk loads; verify transfer and connection limits in the current account. Database capacity does not mean unlimited free compute.
Outbox polling and timers can keep the database active, preventing scale-to-zero. This is acceptable during a session, but set all services to desiredCount=0 afterward to stop queries and allow suspension. Do not query the database for liveness every 10 seconds. Configure active polling and idle backoff; do not promise Neon suspension while queries continue.
Neon is outside the VPC: encrypted Internet traffic, possible billable transfer, and cold starts. Do not use real customer data. Document this teaching limitation rather than calling it a free production architecture.

## Reference estimate, not a quote or hard cap
Checked October 2, 2026; Linux x86 in us-east-1; taxes and credits excluded.
Public Fargate reference: CPU 0.0404784 USD/vCPU-hour; memory 0.004446 USD/GB-hour.
One 0.25 vCPU + 0.5 GB task: 0.0123426 USD/hour for compute.
Public IPv4: 0.005 USD/IP-hour.
Five tasks: (5×0.0123426)+(5×0.005)=0.086713 USD/hour for compute + IPv4.
- 8 total hours: approximately 0.69 USD.
- 40 total hours: approximately 3.47 USD.
- 730 hours: approximately 63.30 USD; do not leave it running all month.
If Gateway uses 1 GB, add 0.002223 USD/hour to those totals. Rolling deployments may temporarily double tasks, which also incur charges. Recalculate using actual sizes before apply.
Additional costs: Cloud Map and its private Route 53 hosted zone, ECR, CloudWatch, transfer to Neon, and possible KMS/SSM charges. A private hosted zone has a monthly fee (initial reference 0.50 USD/month; confirm current pricing), plus discovery resources/queries. SQS advertises 1M free requests/month shared across the account/regions under its terms; count Send/Receive/Delete/visibility actions, not only messages.
Indicative target: 1–5 USD for a short lab with a few dozen running hours and small data/log volumes. This is neither a guarantee nor authorization for recurring monthly spending. AWS credits cover charges only if the account is eligible and has a balance; do not assume credits.

## Required cost controls and operations
- Editable costs.md with region, actual sizes, hours, and estimated extras; preflight prints account/region/resource tags/estimate.
- Default Terraform desired_count=0. Creating infrastructure does not automatically start five services. Explicit startup script.
- stop-lab: sets all services to 0 and verifies zero tasks; preserves the database/queues. ECS does not guarantee automatic shutdown. A local script scheduled for a 2-hour session may fail if the PC shuts down; do not treat it as a hard cap.
- start-lab: validates configuration and owned resources, waits for health, prints IP/mapping, and reminds the user to stop afterward.
- Budget target 5 USD, configurable alerts. A Budget is not a spending limit and does not stop resources by itself. The budget email requires user input; do not invent it.
- destroy-lab: plan/review; target only resources tagged Project=TourMicroservicesLab and Environment=study. Do not delete Neon or schemas by default. Queue/data purging requires a separate explicit option from pause. Understand that destroy removes pending messages.
- CloudWatch retention 3 days; redact payloads. ECR retains 2 recent tags; clean unused images. Delete hosted zone/Cloud Map when finished to remove fixed costs. Stopping tasks does not eliminate all charges.
- No NAT Gateway, ALB, RDS, EFS, EKS, Redis, Secrets Manager, billable private endpoints, or custom KMS key in the base profile.
- Use standard SSM Parameter Store SecureString with a managed key where compatible; verify KMS charges and size limits. Terraform does NOT receive private keys/passwords as variables that would end up in state. Use separate compatible-sized PEM certificate/key parameters (<4 KB for standard parameters); validate limits instead of placing a large PFX in a standard parameter. A script creates sensitive parameters through private input; IaC references existing ARNs. Do not put secrets in logs or shell history.
- Task execution role: ECR pulls, logs, and only required parameters. Task role: Send to authorized destinations and Receive/Delete/ChangeVisibility on its own queue; replay only from its own DLQ. No sqs:* or AdministratorAccess. Gateway never receives business-database credentials.

## Delivery
Versioned Terraform, multistage Dockerfiles, reproducible/pinned builds, .dockerignore, non-root execution where appropriate, ports/health checks, and a one-shot migration script without exposing migrator credentials to runtime. Use the student's AWS profile/SSO, never root access keys. Local build-test script/CI; an optional GitHub/Jenkins pipeline must not block the demo.
AWS is not done until there is real evidence of healthy tasks, the UI, reservation/cancellation, persistence across pause/start, visible costs, and cleanup. Missing credentials/permissions block deployment, not infrastructure generation.

## Official sources
- https://aws.amazon.com/fargate/pricing/
- https://aws.amazon.com/vpc/pricing/
- https://aws.amazon.com/sqs/pricing/
- https://aws.amazon.com/cloud-map/pricing/
- https://aws.amazon.com/route53/pricing/
- https://aws.amazon.com/free/
- https://docs.aws.amazon.com/cost-management/latest/userguide/budgets-managing-costs.html
- https://neon.com/blog/neon-free-plan-1-gb-per-project
- https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-discovery.html
