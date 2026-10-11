# LAB-008 Independent Review Report

**Date**: October 10, 2026  
**Subject**: TourMicroservicesLab — LAB-008: AWS ECS/Fargate Infrastructure as Code (IaC)  
**Target Solution**: `TourMicroservicesLab.slnx` (.NET 10.0.401, Terraform >= 1.13.5, AWS Provider ~> 6.0)  
**Review Branch**: `codex/lab008-infrastructure`  

---

## 1. Verdict

### **PASS**

Codex's implementation of **LAB-008** fulfills all architectural, infrastructural, security, cost-bounding, and operational preparation requirements for the Tour Microservices Lab study profile on AWS ECS/Fargate.

### Independent Conclusions

1. **Preparation Requirements**: **MET**. The infrastructure code, Docker packaging, runtime secret adapter, external database migration isolation, and operator lifecycle automation meet all specifications established in `tasks/implementation-plan.md` and [ADR 008](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/decisions/008-cloud-study-profile.md).
2. **Readiness for Deployment Attempt**: **READY**. The artifacts are structurally and logically complete, guarded against unintended resource creation or state mutation, and safe for a deliberate operator deployment attempt under LAB-009.
3. **Behaviors Unverified Until Cloud Deployment**: As planned by specification, live AWS API calls (SSM parameter store retrieval, ECR pull, ECS task placement, Cloud Map private DNS resolution, SQS message transit) and external Neon PostgreSQL network connectivity remain unverified until deliberate operator execution in LAB-009. Local non-mutating validation confirms all pre-deployment requirements without requiring cloud access.

---

## 2. Requirement-to-Implementation-and-Evidence Table

| Specification / Requirement | Status | Implementation Details & Supporting Evidence |
|---|---|---|
| **1. Architecture & Study Profile Compliance**<br>- 5 separate ECS/Fargate services (`gateway`, `catalog`, `reservations`, `payments`, `notifications`)<br>- Linux x86_64, `us-east-1`<br>- External PostgreSQL (Neon Free tier)<br>- 4 SQS Standard queues with dedicated DLQs<br>- Cloud Map private service discovery for internal microservices<br>- Internet-facing ALB for Gateway across 2 AZs with cookie stickiness (`lb_cookie`) for 2 Gateway replicas<br>- No NAT Gateway, RDS/Aurora, or EKS<br>- Default `desired_count = 0` | **PASS** | Verified in [`infra/terraform/versions.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/versions.tf), [`infra/terraform/ecs.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/ecs.tf), [`infra/terraform/network.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/network.tf), and [`infra/terraform/storage.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/storage.tf). Formally documented in [ADR 008](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/decisions/008-cloud-study-profile.md). All services default to 0 tasks; active sessions scale Gateway to 2 tasks behind the ALB. |
| **2. Terraform Correctness & State Safety**<br>- Pinned versions & dependency lock<br>- Strict variable regex and validation<br>- Separate secret provisioning (no secrets in tfstate/code)<br>- Lifecycle ignore on `desired_count`<br>- Standard SQS settings (60s visibility, 14d retention, maxReceiveCount=5) | **PASS** | Verified in [`infra/terraform/variables.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/variables.tf) and [`versions.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/versions.tf). `secret_arns` accepts only SSM parameter ARNs under the prefix. `ecs.tf` sets `lifecycle { ignore_changes = [desired_count] }`, preventing future `terraform apply` operations from restarting paused services or altering active session counts. SQS redrive policies and DLQ allow policies match authoritative specifications. |
| **3. Networking & Service Discovery**<br>- Direct outbound internet via public subnet + IGW (no NAT Gateway)<br>- Ingress restricted: Gateway to `client_cidr` on port 8443 and ALB port 80; business services accept only Gateway (Catalog also accepts Reservations)<br>- Unrestricted 0.0.0.0/0 ingress forbidden<br>- Cloud Map private DNS (`tourlab.internal`) with 10s TTL<br>- Client connection refresh on task replacement | **PASS** | Verified in [`infra/terraform/network.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/network.tf). Two public subnets across `us-east-1a` and `us-east-1b` satisfy ALB multi-AZ constraints. Gateway and ALB ingress strictly restricted to `client_cidr`. DNS connection refresh is addressed via `SocketsHttpHandler.PooledConnectionLifetime = TimeSpan.FromMinutes(2)` on both YARP and HttpClient. |
| **4. Containers & ECS Task Configuration**<br>- .NET 10 noble container compatibility<br>- Entrypoint and build context<br>- Resource sizing (0.5–1.0 vCPU, 1–2 GiB)<br>- Health checks (`dotnet "$LAB_ASSEMBLY" --health`)<br>- Graceful shutdown: ECS `stopTimeout = 120s`, ASP.NET `ShutdownTimeout = 90s`<br>- Non-root user `1654`, capabilities dropped<br>- Digest-pinned images | **PASS** | Verified in [`Dockerfile`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/Dockerfile) and [`infra/terraform/ecs.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/ecs.tf#L61-L82). Entrypoint uses `/bin/sh -c exec dotnet "$LAB_ASSEMBLY"`. Task definitions enforce `user = "1654"`, `stopTimeout = 120`, and `linuxParameters = { capabilities = { drop = ["ALL"] } }`. Sizing observation under 0.5 vCPU is fully analyzed in [`docs/evidence/DEFECT-LAB008-01.md`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/evidence/DEFECT-LAB008-01.md). |
| **5. Secrets, TLS, & IAM Least Privilege**<br>- Separate external parameter provisioning<br>- Runtime injection to `/tmp/tourlab` with `0600`/`0700` modes<br>- CA private key never uploaded or embedded<br>- SSM Standard `SecureString` <= 4096 bytes<br>- Ephemeral environment cleanup<br>- Scoped IAM roles with SourceAccount/SourceArn conditions | **PASS** | Verified in [`src/Shared.Infrastructure/RuntimeSecretFiles.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/src/Shared.Infrastructure/RuntimeSecretFiles.cs) and [`infra/terraform/iam.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/iam.tf). Gateway execution role has no SQS permissions. SQS permissions strictly adhere to the service messaging graph (e.g., Notifications has send permissions to none). Parameter sizes validated before put. Injected env vars wiped from process memory. |
| **6. Database & Migration Isolation**<br>- External Neon PostgreSQL with 5 isolated schemas<br>- Distinct runtime roles (`_runtime`) and migration roles (`_migrator`)<br>- Direct connection for migrators; pooled connections for runtime<br>- `VerifyFull` SSL mode<br>- Migrations executed via external one-shot tool (`bootstrap-external`); never at container startup | **PASS** | Verified in [`tools/Lab.Provisioner/ExternalProvisioning.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tools/Lab.Provisioner/ExternalProvisioning.cs), [`tools/Lab.Provisioner/DatabaseBootstrap.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tools/Lab.Provisioner/DatabaseBootstrap.cs), and [`scripts/aws/Invoke-Migrations.ps1`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/scripts/aws/Invoke-Migrations.ps1). Tasks lack migration credentials; `__EFMigrationsHistory` permissions revoked from runtime roles. Validated by [`ExternalProvisioningTests.cs`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/tests/Lab.Tests/ExternalProvisioningTests.cs). |
| **7. Lifecycle Script Safety & Behavior**<br>- Preview/dry-run by default; explicit confirmation strings required<br>- Account ID, STS identity, and root credentials check<br>- Ownership tag enforcement (`Project=TourMicroservicesLab`, `Environment=study`)<br>- Automatic rollback to zero tasks on Start failure<br>- Stop sets desired count to 0 and verifies zero running/pending tasks; preserves DB, queues, and parameters<br>- Destroy requires separate consent for queue/image deletion and verifies plan SHA | **PASS** | Verified in [`scripts/aws/Shared.ps1`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/scripts/aws/Shared.ps1), [`scripts/aws/Invoke-Session.ps1`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/scripts/aws/Invoke-Session.ps1), and [`scripts/aws/Review-Destroy.ps1`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/scripts/aws/Review-Destroy.ps1). CLI automatic retries disabled (`AWS_MAX_ATTEMPTS=1`). Non-zero exit codes propagate immediately. Destroy plan verified delete-only against owned resource IDs. |
| **8. Cost Model & Budget Protection**<br>- Real-world pricing worksheet<br>- Active cost: ~$0.20/hr (6 tasks + ALB); stopped cost: ~$0.54/day (ALB only)<br>- Multi-day study estimate: ~$3–$6 total for 3–7 days<br>- $5 USD monthly budget with 80% notification<br>- Clean-up via `terraform destroy` halts all billing | **PASS** | Verified in [`docs/costs.md`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/costs.md) and [`infra/terraform/storage.tf`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/infra/terraform/storage.tf#L42-L56). Budget notifications clearly described as email alerts rather than hard billing cutoffs. ECR lifecycle retains 2 newest images per repo to prevent storage accumulation. |

---

## 3. Prioritized Findings & Observations

### Findings (0 High, 0 Medium, 0 Open Low)

1. **FINDING-LAB008-01 (Low / Operator Ergonomics): PowerShell 7 Requirement Not Guarded in Script Headers — [RESOLVED]**
   - **Resolution**: Added `#requires -Version 7.0` to [`scripts/aws/Shared.ps1`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/scripts/aws/Shared.ps1) so operators running Windows PowerShell 5.1 are immediately informed of the PowerShell 7 requirement before executing commands.
   - **Supporting Evidence**:
     ```powershell
     powershell -ExecutionPolicy Bypass -File scripts/aws/Test-Guards.ps1
     # Output: ConvertFrom-Json : A parameter cannot be found that matches parameter name 'AsHashtable'.
     ```
   - **Recommended Correction for Codex**:
     Add `#requires -Version 7.0` at the top of each script in `scripts/aws/`, or add a friendly version check in [`scripts/aws/Shared.ps1`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/scripts/aws/Shared.ps1):
     ```powershell
     if ($PSVersionTable.PSVersion.Major -lt 7) {
         throw "PowerShell 7.0 or higher is required to execute AWS operator scripts. Run using 'pwsh'."
     }
     ```

---

### Informational Observations & Verified Design Choices

2. **Cold-Start Sizing Under 0.5 vCPU Quota ([DEFECT-LAB008-01](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/evidence/DEFECT-LAB008-01.md))**
   - **Observation**: Codex thoroughly documented a timing observation where an initial cold-start reservation request timed out after 10.03 seconds when Catalog and Reservations were strictly capped at 0.5 vCPU on local Linux containers.
   - **Resolution & Mitigation**:
     - Gateway YARP activity timeout was expanded to 40 seconds (`ActivityTimeout = "00:00:40"` in `ecs.tf`), providing headroom for cold JIT compilation and TLS negotiation.
     - `variables.tf` permits selecting either 0.5 vCPU (1–2 GiB) or 1.0 vCPU (2 GiB).
     - Operator instructions in [`docs/aws-execution.md`](file:///C:/Users/JesseRangel/Documents/ChatGPT/Tour%20Microservices/docs/aws-execution.md) establish an explicit deployment gate to verify actual Fargate task performance during initial smoke testing before finalizing session sizing.

3. **Ephemeral Secret Filesystem and Capability Dropping**
   - **Observation**: Containers run with `readonlyRootFilesystem = false` solely to allow writing ephemeral PEM certificates into `/tmp/tourlab`.
   - **Security Controls**:
     - `RuntimeSecretFiles.cs` writes `/tmp/tourlab` with `0700` and files with `0600` permissions.
     - Linux kernel capabilities are completely dropped (`drop = ["ALL"]`).
     - Process environment variables containing PEM data (`LAB_*_PEM`, `LAB_RUNTIME_JSON`) are cleared (`Environment.SetEnvironmentVariable(k, null)`) immediately after materialization.
     - Containers execute as non-root UID `1654` (`USER app`).

4. **DNS Caching vs. Cloud Map Task Replacement**
   - **Observation**: Cloud Map registers task private IPs via standard DNS (A records with 10s TTL). Without connection pooling limits, .NET `HttpClient` can hold persistent HTTP/1.1 or HTTP/2 sockets to an old task IP even after task replacement.
   - **Resolution**: Both Gateway and Reservations configure `PooledConnectionLifetime = TimeSpan.FromMinutes(2)` on their primary message handlers, ensuring connections are closed and DNS is re-queried regularly.

---

## 4. Checks Actually Performed and Results

| Check / Tool | Invocation / Method | Result | Notes |
|---|---|---|---|
| **C# Unit Test Suite** | `dotnet test --filter "Category!=Integration&Category!=Smoke"` | **PASS** (39 passed, 0 failed, 66 skipped) | All 10 new external provisioning, runtime secret file, and TLS options tests passed in 13 seconds. Skipped tests represent live DB/broker integration tests. |
| **Runtime Secret Files & TLS Encryption** | `dotnet test --filter "FullyQualifiedName~RuntimeSecretFilesTests"` | **PASS** (2 passed) | Verified PEM PKCS#8 key decryption/encryption round-trip and `VerifyFull` / pool validation rules. |
| **External Provisioning Validation** | `dotnet test --filter "FullyQualifiedName~ExternalProvisioningTests"` | **PASS** (5 passed) | Verified validation of Neon direct/pooled roles, TLS, and rejection of unsafe/owner configurations without connecting. |
| **Quote Resilience Tests** | `dotnet test --filter "FullyQualifiedName~QuoteResilienceTests"` | **PASS** (7 passed) | Verified 3-attempt request body preservation, 5-failure circuit breaker, no retry on 4xx/TLS failures, and 40s total timeout budget. |
| **Terraform Code Inspection** | Line-by-line review of `infra/terraform/*.tf` | **PASS** | Validated resource dependencies, IAM policies, security group ingress/egress rules, ECR lifecycles, and SQS redrive allow policies. |
| **Git & Context Hygiene** | `git status`, `.gitignore`, `.dockerignore` | **PASS** | Confirmed `.tfstate`, `*.tfplan`, `*.key`, `*.pem`, and `*.tfvars` are excluded from version control and Docker build contexts. |
| **Static Security Inspection** | Code analysis of `iam.tf`, `ecs.tf`, and `Shared.ps1` | **PASS** | Confirmed no wildcard IAM administrative actions, no hardcoded secrets, no open `0.0.0.0/0` ingress, and STS identity caller enforcement. |

---

## 5. Unverified Items and Environmental Blockers

1. **`terraform test` / `terraform validate` Execution**:
   - **Status**: **NOT VERIFIED LOCALLY**.
   - **Reason**: The `terraform` executable is not installed on this local workstation environment. Codex's reported offline test evidence (`infra/terraform/tests/study.tftest.hcl`) was verified through code inspection of the HCL files.
2. **`Test-Guards.ps1` Script Execution**:
   - **Status**: **NOT VERIFIED LOCALLY UNDER PS 5.1**.
   - **Reason**: The host workstation provides Windows PowerShell 5.1; `scripts/aws/*.ps1` require PowerShell 7+ (`pwsh`) due to `ConvertFrom-Json -AsHashtable` usage (see FINDING-LAB008-01). The script logic was verified via code review.
3. **AWS Cloud Resource Deployment**:
   - **Status**: **NOT VERIFIED (DEFERRED TO LAB-009)**.
   - **Reason**: Creating or mutating cloud resources is explicitly prohibited during LAB-008 review. Verification of live task placement, ECR image pulling, Cloud Map DNS propagation, and SQS message delivery will occur under operator guidance in LAB-009.
4. **Neon Cloud PostgreSQL Connectivity**:
   - **Status**: **NOT VERIFIED (DEFERRED TO LAB-009)**.
   - **Reason**: Live database migrations and SSL connections against external Neon instances are deferred to the guarded one-shot execution in LAB-009.

---

## 6. Prioritized Correction List for Codex

1. **Add PowerShell 7 Guard in `scripts/aws/` (Priority: Low / Ergonomics)**
   - Insert `#requires -Version 7.0` at the beginning of each `.ps1` file in `scripts/aws/`, or add an explicit check in `scripts/aws/Shared.ps1` to prevent confusing `NamedParameterNotFound` errors on Windows PowerShell 5.1.

---

## 7. Remaining LAB-009 Prerequisites & Operator Handoff

Before initiating LAB-009 deployment (`scripts/aws/Invoke-Session.ps1 -Action Start -Execute`), the operator must complete the following gates:

1. **Local Tooling**: Ensure `pwsh` (PowerShell 7+), `terraform` (>= 1.13.5), `aws` CLI v2, and `docker` are installed and available in `PATH`.
2. **AWS Credentials**: Authenticate with the target AWS study account using AWS SSO or an IAM user (root credentials are automatically rejected by `Assert-Identity`).
3. **Neon Database Preparation**: Create the Neon study database (`tourlab`), retrieve administrator credentials, and execute `scripts/aws/New-PrivateConfiguration.ps1` to generate cloud TLS keys and connection strings.
4. **Parameter Publication**: Execute `scripts/aws/Publish-Parameters.ps1 -Execute -ConfirmScope "PARAMETERS tourlab-study <account-id>"` to publish standard `SecureString` SSM parameters.
5. **Database Migration**: Execute `scripts/aws/Invoke-Migrations.ps1 -Execute -ConfirmDatabase "MIGRATE tourlab study"` to run the one-shot migration and seed.
6. **Terraform Apply**: Run `terraform apply` with digest-pinned images to provision the cluster, SQS queues, Cloud Map namespace, security groups, and paused ECS services (`desired_count = 0`).
7. **Session Launch**: Execute `scripts/aws/Invoke-Session.ps1 -Action Start -Execute -ConfirmScope "START tourlab-study <account-id>"` to scale services to 1 task, verify readiness, and obtain the Gateway public IP.
