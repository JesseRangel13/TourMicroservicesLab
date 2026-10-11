locals {
  task_trust = jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Principal = { Service = "ecs-tasks.amazonaws.com" }, Action = "sts:AssumeRole", Condition = { StringEquals = { "aws:SourceAccount" = var.account_id }, ArnLike = { "aws:SourceArn" = "arn:aws:ecs:${var.region}:${var.account_id}:*" } } }] })
}
resource "aws_iam_role" "execution" {
  for_each           = local.services
  name               = "${var.prefix}-${each.key}-execution"
  assume_role_policy = local.task_trust
}
resource "aws_iam_role_policy" "execution" {
  for_each = local.services
  name     = "${var.prefix}-execution"
  role     = aws_iam_role.execution[each.key].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    { Effect = "Allow", Action = ["ecr:GetAuthorizationToken"], Resource = "*" },
    { Effect = "Allow", Action = ["ecr:BatchCheckLayerAvailability", "ecr:GetDownloadUrlForLayer", "ecr:BatchGetImage"], Resource = aws_ecr_repository.service[each.key].arn },
    { Effect = "Allow", Action = ["logs:CreateLogStream", "logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.service[each.key].arn}:*" },
    { Effect = "Allow", Action = ["ssm:GetParameters"], Resource = values(var.secret_arns[each.key]) }
  ] })
}
resource "aws_iam_role" "application" {
  for_each           = local.services
  name               = "${var.prefix}-${each.key}-application"
  assume_role_policy = local.task_trust
}
resource "aws_iam_role_policy" "transport" {
  for_each = local.business
  name     = "${var.prefix}-owned-transport"
  role     = aws_iam_role.application[each.key].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    { Effect = "Allow", Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes", "sqs:GetQueueUrl"], Resource = [aws_sqs_queue.input[each.key].arn, aws_sqs_queue.dlq[each.key].arn] },
    { Effect = "Allow", Action = ["sqs:SendMessage"], Resource = [for destination in toset(concat([each.key], local.destinations[each.key])) : aws_sqs_queue.input[destination].arn] }
  ] })
}
