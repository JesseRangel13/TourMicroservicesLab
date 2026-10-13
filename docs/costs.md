# Study cost worksheet

## EXT-001/002 default profile — checked 2026-10-11

The extension default is `enable_alb_study_profile=false`: five0.5vCPU/1GiB tasks plus public IPv4 cost USD0.148426/hour, USD0.296852 for two hours and USD2.374816 for16active hours before supporting costs. The older six-task/ALB worksheet below is retained for the opt-in teaching profile; it does not describe the default. No resource or paid plan was activated.

Additional us-east-1 reference inputs, without credits/free allowances:

| Extension item | Rate / billing input |
|---|---|
| S3 Standard storage | USD0.023/GB-month first tier |
| S3 PUT / GET+HEAD | USD0.005/1,000 PUT; USD0.0004/1,000 GET/HEAD; count retries/reconciliation |
| DynamoDB Standard on-demand | USD0.625/million write units, USD0.125/million read units, USD0.25/GB-month storage |
| GSI | Adds stored bytes and write units for projected updates; ALL duplicates the card data |
| Encryption | SSE-S3 and default DynamoDB AWS-owned encryption; no new customer KMS key. Existing SSM managed-key usage remains separately billable. |
| Extra queue/DLQ | Standard request charges; includes empty polling, ACK/visibility and replay, not just published messages |
| Transfer | Add actual image Internet egress and interservice/provider transfer; browser downloads original bytes even when rendered160px wide |

S3 regional rates were read from the [official current us-east-1 price list](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonS3/current/us-east-1/index.json), for TimedStorage-ByteHrs and Requests-Tier1/2. [S3 pricing](https://aws.amazon.com/s3/pricing/), [DynamoDB pricing](https://aws.amazon.com/dynamodb/pricing/), [DynamoDB encryption](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/EncryptionAtRest.html), [SQS pricing](https://aws.amazon.com/sqs/pricing/).

Illustrative small month:0.02GB S3 storage (USD0.00046),200PUTs (USD0.001),2,000GET/HEADs (USD0.0008);1,000booking updates assumed2KB in both main item and GSI (4,000write units, USD0.0025),10,000read units (USD0.00125),0.02GB combined table/index storage (USD0.005). Subtotal **USD0.01101**, excluding extra SQS, conditional failures/retries/rebuilds, logs and transfer. Actual item sizes, consistency/read rounding and GSI writes determine units. This is an input example, not a spending cap or free-service promise.

Keep sample images well below100KB and total stored images below25MiB during exercises; upload upper limits are safety bounds, not recommended fixture sizes. A two-hour session adds about USD0.30 of task/IP cost; retain the hosted-zone/ECR/log worksheet below for other fixed costs. Stop eliminates task activity after zero tasks is verified, but S3/DynamoDB stored data still bills. No automatic image expiry/live-table TTL is enabled because it would discard legitimate study data; deletion requires explicit reviewed operations. DynamoDB protection intentionally prevents casual whole-stack destruction. Budgets remain alerts, not enforcement.

## Earlier opt-in ALB worksheet

Checked official sources on 2026-10-10. Region us-east-1, Linux x86_64 on-demand, USD before tax, no credits/Free Tier assumed. Replace inputs after observing an actual AWS session; these are estimates, not measured bills.

Inputs: six tasks (2 Gateway replicas + 4 business services), each 0.5 vCPU and 1 GiB; one public IPv4 per task; one internet-facing Application Load Balancer (ALB) for Gateway study. Rates: CPU 0.0404784/vCPU-hour, memory 0.004446/GB-hour, public IPv4 0.005/IP-hour, ALB 0.0225/hour + LCU (~0.008/LCU-hour). [Fargate pricing](https://aws.amazon.com/fargate/pricing/), [VPC pricing](https://aws.amazon.com/vpc/pricing/), [ALB pricing](https://aws.amazon.com/elasticloadbalancing/pricing/).

`tasks_hourly = 6 * (0.5 * 0.0404784 + 1 * 0.004446 + 0.005) = 0.178111`  
`alb_hourly = 0.0225`  
`total_active_hourly = 0.200611`

| Scenario | Active Compute + IPv4 + ALB USD |
|---|---:|
| Two-hour session | 0.401222 |
| Eight sessions / 16 hours active | 3.209776 |
| Three-day study session (ALB 72h + 8h active tasks) | 3.045000 |
| Seven-day study session (ALB 168h + 16h active tasks) | 6.630000 |
| Stopped environment (ALB only, 24h) | 0.540000 / day |

Image download/startup and graceful termination contribute billable time. Fargate includes 20 GiB ephemeral storage per task; no extra storage is requested. The ALB enables hands-on study of multi-AZ load balancing and Blazor cookie stickiness across 2 replicas; because it bills continuously at $0.0225/hour until destroyed, operators should run `terraform destroy` when the multi-day study evaluation concludes.

Supporting costs, deliberately budgeted without free allowances:

| Item | Worksheet input / treatment |
|---|---|
| Private hosted zone | USD0.50/month, not prorated; remains while paused. Private DNS queries have no Route53 query fee. |
| Cloud Map registry | USD0.10/resource-month reference; up to five active registrations, up to USD0.50 for a full month, plus overlap. DNS discovery is used; no DiscoverInstances HTTP polling. Verify actual registration-time billing. |
| ECR storage | USD0.10/GB-month reference; enter actual stored compressed GB. At2GB, USD0.20/month. Retaining two images per repository bounds history, not image size. |
| CloudWatch | Reference USD0.50/GB standard ingestion, USD0.03/compressedGB-month archive plus query charges. At0.1GB ingestion, USD0.05 before storage. Three-day retention bounds storage, not ingestion volume. |
| SQS | Enter Send/Receive/Delete/visibility request counts, including empty polls and duplicates; use regional Standard pricing calculator. Do not subtract the advertised account-wide1M request allowance without verifying shared consumption. |
| SSM/KMS | Standard parameters/default throughput have no parameter-storage charge. AWS-managed key storage has no charge; KMS requests can be billed. Symmetric usage reference USD0.03/10,000requests:1,000requests -> USD0.003, without allowance. No customer key is created. |
| Transfer | Enter actual outbound bytes to Neon/PC and regional Internet-transfer rate; do not assume credits or free transfer eligibility. Same-region ECR image transfer has no ECR transfer fee. |

Sources: [Route53](https://aws.amazon.com/route53/pricing/), [Cloud Map](https://aws.amazon.com/cloud-map/pricing/), [ECR](https://aws.amazon.com/ecr/pricing/), [CloudWatch](https://aws.amazon.com/cloudwatch/pricing/), [SQS](https://aws.amazon.com/sqs/pricing/), [Systems Manager](https://aws.amazon.com/systems-manager/pricing/), [KMS](https://aws.amazon.com/kms/pricing/). Cloud Map's general DNS example lists query charges; the more specific current Route53 private-zone rule says private queries are free. Use that rule for this private namespace; confirm billing in LAB-009.

Illustrative16hour month: USD2.374816 + zone0.50 + conservative registry0.50 + ECR0.20 + ingestion0.05 + KMS0.003 = **USD3.627816**, before SQS, log storage/queries and transfer. This is a small-volume input scenario, not a ceiling. Continuous compute/IP alone is USD108.35, and supporting usage rises. Rolling deployments may temporarily double capacity and disconnect Gateway circuits.

Desired_count0 removes task compute/public-IP charges after tasks stop, but hosted zone, stored images/logs and any retained registry resources can still bill. Budget defaults to USD5, with actual80% email warning and an operator-provided address. It is account-wide to avoid hiding untagged/shared charges. Alerts can lag and never stop services. No automatic cutoff is implemented; a locally scheduled stop fails if the PC/network/credentials become unavailable. Explicitly verify zero tasks after every session.

Neon Free: the official [October2 announcement](https://neon.com/blog/neon-free-plan-1-gb-per-project) states1GB/project and100CU-hours/month. Older0.5GB documentation is superseded by that announcement; check the actual account console before use, including transfer/connection limits. Keep this dataset below100MB and never enable paid overages automatically. Outbox/recovery polling keeps compute active; stopping all services allows suspension. No cloud database connection or quota verification was performed in LAB-008.
