# LAB-008 execution handoff

This is a procedure for a separately authorized LAB-009 deployment. LAB-008 created no AWS resources, published no images/parameters and made no Neon connection. AC-30 is unverified. Local results: docs/evidence/LAB-008.md. Design: docs/decisions/008-cloud-study-profile.md. Costs: docs/costs.md.

## Deployment gates

1. Obtain independent review and address any remaining LAB-007 browser evidence. Use an isolated study account with SSO/profile, not root keys or production. Verify Fargate vCPU/IP quotas, region us-east-1 and budget notification permissions. Supply your real email and current public client /32. No paid plan is authorized by these files.
2. Manually create/choose a Neon Free study project near us-east-1 and database `tourlab`. Verify current free limits and provider permissions. Dataset <100MB. Confirm CREATEROLE/schema capabilities, direct migration endpoint, VerifyFull hostname/root trust, pooler compatibility and five bounded runtime pools. One engine does not imply shared schemas.
3. Build Release and all five images locally. Preserve the existing Dockerfile's pinned .NET10 runtime, non-root `app`, exec PID1, and included `dotnet assembly --health` tooling. LAB-009 publishes versioned ECR tags, resolves digests and supplies all five digests in private tfvars. No latest tags. Repository-only bootstrap is described in infra/terraform/README.md.
4. Generate cloud private inputs separately from the local lab:

```powershell
pwsh -File scripts/aws/New-PrivateConfiguration.ps1 -DirectHost ep-YOUR-ENDPOINT.us-east-1.aws.neon.tech -RuntimeHost ep-YOUR-ENDPOINT-pooler.us-east-1.aws.neon.tech
dotnet run --project tools/Lab.Provisioner -c Release --no-build -- validate-external .local/aws/provisioner.external.json
```

The prompt keeps passwords out of shell arguments/history. Cloud leaf SANs include `lab.tours.test`, localhost and service.tourlab.internal; generated CA private key stays on the PC. Review generated settings and seed users in ignored files, never commit them. Windows ACLs/Linux directory permissions restrict the cloud input directory. Securely back up CA/JWT/Data Protection private keys and private state; losing protection material can invalidate existing cookies/key rings.

5. With all services paused, run the one-shot migration preview, then the separately authorized explicit invocation:

```powershell
pwsh -File scripts/aws/Invoke-Migrations.ps1
# Future LAB-009 only:
pwsh -File scripts/aws/Invoke-Migrations.ps1 -Execute -ConfirmDatabase 'MIGRATE tourlab study'
```

Bootstrap orders roles/schemas, service-owned migration histories, migrations, runtime object grants and configured Identity seeding. Each schema uses its own migrator; no migrator/admin credential goes to Fargate. A failure can leave earlier migrations committed: inspect private logs and schema histories, correct permissions/version, rerun the idempotent provisioner. Never delete existing data to recover. Test additive/backward-compatible migrations first; incompatible migrations require all tasks stopped and a reviewed backup/restore procedure. Check runtime roles cannot select/update other schemas, cannot create tables or inherit admin membership, and can perform only their owned operations. Existing local permission tests do not prove Neon role compatibility. Block startup if these checks fail.

For each of the five runtime identities, connect with a verified-TLS SQL client using its private connection entry (password prompt/private profile, never shell arguments). Run these read-only inspections and save only booleans/role names, not connection values:

```sql
SELECT current_user, ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid();
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole, rolinherit
FROM pg_roles WHERE rolname = current_user;
SELECT nspname, has_schema_privilege(current_user, oid, 'USAGE') AS can_use,
       has_schema_privilege(current_user, oid, 'CREATE') AS can_create
FROM pg_namespace WHERE nspname IN ('catalog','reservations','payments','notifications','identity');
SELECT rolname FROM pg_roles
WHERE oid <> (SELECT oid FROM pg_roles WHERE rolname = current_user)
  AND pg_has_role(current_user, oid, 'MEMBER');
```

Expect ssl=true, all four role privilege flags false, only owned schema USAGE true and CREATE false, and no unexpected memberships that could permit SET ROLE. For reservations_runtime, `SELECT 1 FROM catalog."Tours" LIMIT 1` must fail with42501; repeat foreign-table SELECT checks across every ordered pair of schemas (identity's table is `"AspNetUsers"`). Check table/sequence DML permissions with has_table_privilege/has_sequence_privilege without modifying data. Refuse startup if a provider role grants access beyond this boundary. Do not insert fake rows or delete data just to test permissions.

6. Populate private Terraform vars and session inventory with exact per-service parameter paths. Run Publish-Parameters without Execute first, then explicitly confirm `PARAMETERS PREFIX ACCOUNT`. Values are Standard SecureString using alias/aws/ssm, <=4096 UTF-8 bytes each; never publish CA private key, administrator or migrator connections. Gateway gets identity_runtime only. Updating parameters requires new tasks to refresh injection.
7. Review the real Terraform plan and private state location, confirm no unplanned resources/costs, then request separate provisioning authorization. Initial desired counts remain zero. Export the session inventory after provisioning. Neither provisioning nor deployment was executed in LAB-008.

## Study lifecycle (LAB-009 only)

```powershell
pwsh -File scripts/aws/Invoke-Session.ps1 -Action Preflight -SessionFile .local/aws/session.json
pwsh -File scripts/aws/Invoke-Session.ps1 -Action Preflight -SessionFile .local/aws/session.json -ReadOnlyAws
pwsh -File scripts/aws/Invoke-Session.ps1 -Action Start -SessionFile .local/aws/session.json -Execute -ConfirmScope 'START tourlab-study YOURACCOUNT'
pwsh -File scripts/aws/Invoke-Session.ps1 -Action Stop -SessionFile .local/aws/session.json -Execute -ConfirmScope 'STOP tourlab-study YOURACCOUNT'
```

Substitute the exact configured prefix/account, including all 12 digits. Start checks owned tags, immutable image existence and Standard SecureString metadata, enables 2 Gateway tasks and 1 task per business service, and waits for stable services and verified-TLS authenticated readiness. Failure attempts to return all services to zero; a rollback failure explicitly leaves billing unverified. Readiness may fail on Neon cold start: inspect prerequisites, confirm zero tasks, then retry deliberately. Start prints both the ALB Gateway URL (e.g. `http://tourlab-alb-...elb.amazonaws.com`) and the direct Gateway task IP. For direct task access, update the PC hosts file to `IP lab.tours.test`, trust the **public** lab CA in the PC trust store and browse https://lab.tours.test:8443. For ALB access, browse directly using the ALB URL over HTTP port 80 (ingress restricted to your `client_cidr`). Private Cloud Map DNS is accessible inside the VPC. Curl uses --resolve and --cacert for direct TLS.

Runtime configuration is nonsecret LAB_RUNTIME_JSON plus per-service SSM-injected PEM/ConnectionStrings__Runtime. Files materialize into /tmp/tourlab; public roots plus lab CA are trusted through SSL_CERT_FILE. All application ports are 8443. `/health/live` is dependency-free and drives ECS container health; `/health/ready` checks the owned database on demand and serves as the ALB target group health check. No periodic database health scraper is introduced. stopTimeout 120s exceeds application shutdown 90s, leaving room for worker cancellation and lease recovery. An interrupted message can be redelivered; Inbox/business idempotency still protects effects.

Two Gateway task instances serve traffic behind the internet-facing ALB. Target Group stickiness (`lb_cookie`) ensures that stateful Blazor Interactive Server SignalR circuits remain pinned to the appropriate task instance across subsequent requests while distributing concurrent sessions across both tasks. Connection recycling accommodates private DNS changes. API route allowlists, TLS, JWT validation, admin policies and owner-hiding 404s remain active.

Stop verifies desired/running/pending counts and all tasks in the owned cluster are zero. It preserves PostgreSQL, queues, images, parameters and logs. Terraform ignores desired_count changes so later plans do not silently restart a paused lab. Stop every study session; active database clients/workers can prevent Neon suspension. Because the ALB incurs a fixed ~$0.0225/hr ($0.54/day) charge even when tasks are stopped, operators should run `Review-Destroy.ps1` to destroy the infrastructure when the multi-day study evaluation concludes. Budget email is an alert, not a cutoff.

## Review and destruction

Default Review-Destroy is offline. `-PreparePlan -AllowQueueDeletion -AllowImageDeletion` reads owned resources, requires stopped tasks and produces a private destroy plan and SHA256. Review its exact resource list and queued-work loss. `-Execute` requires the same separate consent flags, exact `DESTROY PREFIX ACCOUNT` confirmation and `-ReviewedPlanSha256`. The saved plan must contain only deletions of matching owned state resources with unchanged identifiers and valid scope/tags. Unexpected modules/resources/tags are rejected. Never pass another project's state.

ECR force_delete=false deliberately blocks nonempty repository deletion. Image consent does not automatically purge repositories: remove only reviewed owned image digests separately before destruction, or destruction may partially fail. Queue deletion removes pending messages; normal Stop never purges queues. No database/schema deletion or queue purge script is supplied. External Neon and separately provisioned SSM parameters are outside Terraform and retained. At the end of the course, separately review removal of those scoped study parameters and the Neon study project if desired. Do not remove unrelated resources. Verify billing and residual fixed resources after cleanup.

## Certificate renewal

Check certificate expiry before a session. Preserve the CA/JWT/Data Protection material for ordinary leaf renewal, issue leaves for the same SANs, validate their key matches/size, publish reviewed owned parameters and explicitly replace tasks. A CA rotation requires updating client/public-root bundles together. JWT or protection-key rotation affects tokens/cookies and needs its own compatibility procedure; do not run the fresh-generation script over an existing directory.

## Remaining cloud verification

Record the real reviewed plan, allowed-account identity, parameter injection, role/TLS denial checks, healthy tasks, actual IP/circuit behavior, booking/cancellation, duplicate/reordered messages, persisted data after stop/start, observed spend and final zero-task/cleanup results in LAB-009. No local test substitutes for this evidence. CI remains a local-backend validation/image-build workflow and has no deployment/cloud credentials.
