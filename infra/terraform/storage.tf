resource "aws_ecr_repository" "service" {
  for_each             = local.services
  name                 = "${var.prefix}/${each.key}"
  image_tag_mutability = "IMMUTABLE"
  force_delete         = false
  image_scanning_configuration {
    scan_on_push = true
  }
}
resource "aws_ecr_lifecycle_policy" "service" {
  for_each   = local.services
  repository = aws_ecr_repository.service[each.key].name
  policy     = jsonencode({ rules = [{ rulePriority = 1, description = "Retain two newest versioned images", selection = { tagStatus = "any", countType = "imageCountMoreThan", countNumber = 2 }, action = { type = "expire" } }] })
}
resource "aws_cloudwatch_log_group" "service" {
  for_each          = local.services
  name              = "/ecs/${var.prefix}/${each.key}"
  retention_in_days = 3
}
resource "aws_sqs_queue" "dlq" {
  fifo_queue                = false
  for_each                  = local.business
  name                      = "${var.prefix}-${each.key}-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}
resource "aws_sqs_queue" "input" {
  fifo_queue                 = false
  for_each                   = local.business
  name                       = "${var.prefix}-${each.key}"
  message_retention_seconds  = 1209600
  visibility_timeout_seconds = 60
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy             = jsonencode({ deadLetterTargetArn = aws_sqs_queue.dlq[each.key].arn, maxReceiveCount = 5 })
}
resource "aws_sqs_queue_redrive_allow_policy" "owned" {
  for_each             = local.business
  queue_url            = aws_sqs_queue.dlq[each.key].url
  redrive_allow_policy = jsonencode({ redrivePermission = "byQueue", sourceQueueArns = [aws_sqs_queue.input[each.key].arn] })
}
resource "aws_budgets_budget" "study" {
  name         = "${var.prefix}-monthly"
  budget_type  = "COST"
  limit_amount = tostring(var.monthly_budget_usd)
  limit_unit   = "USD"
  time_unit    = "MONTHLY"
  # Account-wide alert includes charges omitted by imperfect resource cost tagging.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 80
    threshold_type             = "PERCENTAGE"
    notification_type          = "ACTUAL"
    subscriber_email_addresses = [var.budget_email]
  }
}
