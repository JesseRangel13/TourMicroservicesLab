output "session" {
  value = { prefix = var.prefix, account_id = var.account_id, region = var.region, cluster = aws_ecs_cluster.lab.name,
    services           = { for name, resource in aws_ecs_service.service : name => resource.name },
    image_repositories = { for name, resource in aws_ecr_repository.service : name => resource.repository_url },
    secret_arns        = var.secret_arns, images = var.images, task_sizes = var.task_sizes,
  tags = local.tags, hostname = "lab.tours.test", port = 8443,
  alb_dns_name = aws_lb.gateway.dns_name, alb_endpoint = "http://${aws_lb.gateway.dns_name}" }
}
