mock_provider "aws" {
  mock_resource "aws_iam_role" {
    defaults = { arn = "arn:aws:iam::111111111111:role/tourlab-mock" }
  }
  mock_resource "aws_ecs_cluster" {
    defaults = { arn = "arn:aws:ecs:us-east-1:111111111111:cluster/tourlab-study", id = "arn:aws:ecs:us-east-1:111111111111:cluster/tourlab-study" }
  }
  mock_resource "aws_ecs_task_definition" {
    defaults = { arn = "arn:aws:ecs:us-east-1:111111111111:task-definition/tourlab-mock:1" }
  }
  mock_resource "aws_service_discovery_service" {
    defaults = { arn = "arn:aws:servicediscovery:us-east-1:111111111111:service/srv-mock" }
  }
  mock_resource "aws_sqs_queue" {
    defaults = { arn = "arn:aws:sqs:us-east-1:111111111111:tourlab-mock", url = "https://sqs.us-east-1.amazonaws.com/111111111111/tourlab-mock" }
  }
  mock_resource "aws_cloudwatch_log_group" {
    defaults = { arn = "arn:aws:logs:us-east-1:111111111111:log-group:/ecs/tourlab-mock" }
  }
  mock_resource "aws_ecr_repository" {
    defaults = { arn = "arn:aws:ecr:us-east-1:111111111111:repository/tourlab-mock" }
  }
  mock_resource "aws_lb" {
    defaults = { arn = "arn:aws:elasticloadbalancing:us-east-1:111111111111:loadbalancer/app/tourlab-mock/123", dns_name = "tourlab-alb-mock.us-east-1.elb.amazonaws.com" }
  }
  mock_resource "aws_lb_target_group" {
    defaults = { arn = "arn:aws:elasticloadbalancing:us-east-1:111111111111:targetgroup/tourlab-mock/456" }
  }
  mock_resource "aws_lb_listener" {
    defaults = { arn = "arn:aws:elasticloadbalancing:us-east-1:111111111111:listener/app/tourlab-mock/123/789" }
  }
}

run "offline_study_plan" {
  # Mock application only: no credentials, SDK calls or AWS resources.
  command = apply
  assert {
    condition     = length(aws_ecs_service.service) == 5 && alltrue([for s in aws_ecs_service.service : s.desired_count == 0])
    error_message = "Exactly five services must start paused."
  }
  assert {
    condition     = length(aws_sqs_queue.input) == 4 && length(aws_sqs_queue.dlq) == 4 && alltrue([for q in aws_sqs_queue.input : q.message_retention_seconds == 1209600 && q.visibility_timeout_seconds == 60])
    error_message = "Four Standard queues/DLQs and retained duplicate protection required."
  }
  assert {
    condition     = alltrue([for t in aws_ecs_task_definition.service : t.network_mode == "awsvpc" && jsondecode(t.container_definitions)[0].user == "1654" && jsondecode(t.container_definitions)[0].stopTimeout == 120])
    error_message = "Non-root Fargate tasks need bounded graceful shutdown."
  }
  assert {
    condition     = length(aws_vpc_security_group_ingress_rule.gateway) == 4 && aws_vpc_security_group_ingress_rule.client.cidr_ipv4 == var.client_cidr
    error_message = "Business ingress must use Gateway groups; only authorized client ingress is public."
  }
  assert {
    condition     = alltrue([for log in aws_cloudwatch_log_group.service : log.retention_in_days == 3]) && alltrue([for repo in aws_ecr_repository.service : repo.image_tag_mutability == "IMMUTABLE" && !repo.force_delete])
    error_message = "Bound logs and preserve immutable images against accidental destruction."
  }
  assert {
    condition = !contains(keys(aws_iam_role_policy.transport), "gateway") && alltrue([for name, task in aws_ecs_task_definition.service :
      toset([for secret in jsondecode(task.container_definitions)[0].secrets : secret.name]) == toset(local.required_secrets[name]) &&
    !strcontains(task.container_definitions, "CA_PRIVATE") && !strcontains(task.container_definitions, "migrator")])
    error_message = "Gateway must have no queue role and tasks must receive only their runtime secret bundle."
  }
  assert {
    condition     = alltrue([for queue in aws_sqs_queue.input : !queue.fifo_queue]) && alltrue([for queue in aws_sqs_queue.dlq : !queue.fifo_queue])
    error_message = "The transport explicitly uses Standard queues and tolerates duplication/reordering."
  }
}
run "reject_public_ingress" {
  command = plan
  variables { client_cidr = "0.0.0.0/0" }
  expect_failures = [var.client_cidr]
}
run "reject_unmeasured_small_tasks" {
  command = plan
  variables {
    task_sizes = { gateway = { cpu = 256, memory = 512 }, catalog = { cpu = 256, memory = 512 }, reservations = { cpu = 256, memory = 512 }, payments = { cpu = 256, memory = 512 }, notifications = { cpu = 256, memory = 512 } }
  }
  expect_failures = [var.task_sizes]
}
