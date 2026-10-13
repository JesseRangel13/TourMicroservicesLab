locals {
  shared_secret_names = ["LAB_CA_PEM", "LAB_TLS_CERT_PEM", "LAB_TLS_KEY_PEM", "LAB_JWT_PUBLIC_PEM", "ConnectionStrings__Runtime"]
  required_secrets = { for service in local.services : service => concat(local.shared_secret_names,
    service == "gateway" ? ["LAB_JWT_PRIVATE_PEM", "LAB_PROTECTION_CERT_PEM", "LAB_PROTECTION_KEY_PEM"] : [],
  service == "catalog" ? ["LAB_SERVICE_PUBLIC_PEM"] : [], service == "reservations" ? ["LAB_SERVICE_PRIVATE_PEM", "BookingProjection__CursorKey"] : []) }
  queue_urls = merge({ for service in local.business : "tourlab-${service}" => aws_sqs_queue.input[service].url }, { "tourlab-booking-projection" = aws_sqs_queue.booking_projection.url })
  runtime = { for service in local.services : service => merge({
    AllowedHosts       = service == "gateway" && var.enable_alb_study_profile ? "localhost;lab.tours.test;gateway;gateway.tourlab.internal;*.elb.amazonaws.com;*" : "localhost;lab.tours.test;${service};${service}.tourlab.internal",
    LabFeaturesEnabled = true,
    ConnectionStrings  = { TrustSystemRootCertificates = true },
    Jwt                = merge({ PublicKeyPath = "/tmp/tourlab/jwt.pub" }, service == "gateway" ? { PrivateKeyPath = "/tmp/tourlab/jwt.key" } : {}),
    Kestrel            = { Endpoints = { Https = { Url = "https://0.0.0.0:8443", Certificate = { Path = "/tmp/tourlab/tls.crt", KeyPath = "/tmp/tourlab/tls.key" } } } }
    }, jsondecode(service == "gateway" ? jsonencode({
      DataProtection = { CertificatePath = "/tmp/tourlab/protection.crt", KeyPath = "/tmp/tourlab/protection.key" },
      GatewayApi     = { BaseAddress = "https://localhost:8443/" },
      ReverseProxy = {
        Routes   = { for name in local.business : name => { ClusterId = name, AuthorizationPolicy = "Api", Match = { Path = "/api/${name}/v1/{**rest}" }, Transforms = [{ PathRemovePrefix = "/api/${name}" }] } },
        Clusters = { for name in local.business : name => { Destinations = { primary = { Address = "https://${name}.tourlab.internal:8443/" } }, HttpClient = { MaxConnectionsPerServer = 10 }, HttpRequest = { ActivityTimeout = "00:00:40" } } }
      }
    }) : "{}"), service == "catalog" ? { ServiceJwt = { PublicKeyPath = "/tmp/tourlab/service.pub" }, ImageStorage = { Enabled = true, Local = false, Bucket = aws_s3_bucket.tour_images.id, Region = var.region } } : {}, service == "reservations" ? {
    BookingProjection                               = { Enabled = true, Local = false, Table = aws_dynamodb_table.bookings.name, Region = var.region }
    CatalogClient                                   = { BaseAddress = "https://catalog.tourlab.internal:8443/", PrivateKeyPath = "/tmp/tourlab/service.key", TotalTimeoutSeconds = 10 }
    } : {}, jsondecode(contains(local.business, service) ? jsonencode({ Messaging = {
      Enabled            = true, Local = false, Region = var.region, InputQueue = "tourlab-${service}", QueueUrls = local.queue_urls,
      DeadLetterQueueUrl = aws_sqs_queue.dlq[service].url, LeaseSeconds = 60, VisibilitySeconds = 60, WaitSeconds = 20
  } }) : "{}")) }
}
resource "aws_ecs_cluster" "lab" {
  name = var.prefix
}
resource "aws_service_discovery_private_dns_namespace" "lab" {
  name        = "tourlab.internal"
  description = "Private study discovery; not an HTTP load balancer"
  vpc         = aws_vpc.lab.id
}
resource "aws_service_discovery_service" "service" {
  for_each = local.services
  name     = each.key
  dns_config {
    namespace_id   = aws_service_discovery_private_dns_namespace.lab.id
    routing_policy = "MULTIVALUE"
    dns_records {
      ttl  = 10
      type = "A"
    }
  }
  health_check_custom_config {}
}
resource "aws_ecs_task_definition" "service" {
  for_each                 = local.services
  family                   = "${var.prefix}-${each.key}"
  network_mode             = "awsvpc"
  requires_compatibilities = ["FARGATE"]
  cpu                      = tostring(var.task_sizes[each.key].cpu)
  memory                   = tostring(var.task_sizes[each.key].memory)
  execution_role_arn       = aws_iam_role.execution[each.key].arn
  task_role_arn            = aws_iam_role.application[each.key].arn
  runtime_platform {
    cpu_architecture        = "X86_64"
    operating_system_family = "LINUX"
  }
  container_definitions = jsonencode([{
    name = each.key, image = var.images[each.key], essential = true, user = "1654", stopTimeout = 120,
    # app needs only ephemeral /tmp for materialized secret files; no persistent volume.
    readonlyRootFilesystem = false,
    linuxParameters        = { capabilities = { drop = ["ALL"] } },
    portMappings           = [{ containerPort = 8443, hostPort = 8443, protocol = "tcp" }],
    environment            = [{ name = "LAB_RUNTIME_JSON", value = jsonencode(local.runtime[each.key]) }, { name = "SSL_CERT_FILE", value = "/tmp/tourlab/trust.crt" }],
    secrets                = [for name, arn in var.secret_arns[each.key] : { name = name, valueFrom = arn }],
    healthCheck            = { command = ["CMD", "dotnet", "${local.assemblies[each.key]}.dll", "--health"], interval = 30, timeout = 8, retries = 3, startPeriod = 60 },
    logConfiguration       = { logDriver = "awslogs", options = { awslogs-group = aws_cloudwatch_log_group.service[each.key].name, awslogs-region = var.region, awslogs-stream-prefix = "ecs" } }
  }])
  lifecycle {
    precondition {
      condition     = toset(keys(var.secret_arns[each.key])) == toset(local.required_secrets[each.key]) && alltrue([for name, arn in var.secret_arns[each.key] : arn == "arn:aws:ssm:${var.region}:${var.account_id}:parameter/${var.prefix}/${each.key}/${name}"])
      error_message = "Secret references must exactly match this service's least-privilege bundle, account and prefix."
    }
    precondition {
      condition     = startswith(var.images[each.key], "${var.account_id}.dkr.ecr.${var.region}.amazonaws.com/${var.prefix}/${each.key}@sha256:")
      error_message = "Image must belong to this service, account and study prefix."
    }
  }
}
resource "aws_lb" "gateway" {
  count              = var.enable_alb_study_profile ? 1 : 0
  name               = "${var.prefix}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb[0].id]
  subnets            = [aws_subnet.public.id, aws_subnet.public_b.id]
  tags               = { Name = "${var.prefix}-alb" }
}

resource "aws_lb_target_group" "gateway" {
  count       = var.enable_alb_study_profile ? 1 : 0
  name        = substr("${var.prefix}-gtw-tg", 0, 32)
  port        = 8443
  protocol    = "HTTPS"
  vpc_id      = aws_vpc.lab.id
  target_type = "ip"

  health_check {
    path                = "/health/ready"
    protocol            = "HTTPS"
    interval            = 30
    timeout             = 8
    healthy_threshold   = 2
    unhealthy_threshold = 3
    matcher             = "200"
  }

  stickiness {
    type            = "lb_cookie"
    cookie_duration = 86400
    enabled         = true
  }
}

resource "aws_lb_listener" "gateway" {
  count             = var.enable_alb_study_profile ? 1 : 0
  load_balancer_arn = aws_lb.gateway[0].arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.gateway[0].arn
  }
}

resource "aws_ecs_service" "service" {
  for_each                           = local.services
  name                               = "${var.prefix}-${each.key}"
  cluster                            = aws_ecs_cluster.lab.id
  task_definition                    = aws_ecs_task_definition.service[each.key].arn
  desired_count                      = 0
  launch_type                        = "FARGATE"
  platform_version                   = "1.4.0"
  enable_ecs_managed_tags            = true
  propagate_tags                     = "SERVICE"
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }
  network_configuration {
    subnets          = [aws_subnet.public.id, aws_subnet.public_b.id]
    security_groups  = [aws_security_group.service[each.key].id]
    assign_public_ip = true
  }
  service_registries {
    registry_arn = aws_service_discovery_service.service[each.key].arn
  }
  dynamic "load_balancer" {
    for_each = each.key == "gateway" && var.enable_alb_study_profile ? [1] : []
    content {
      target_group_arn = aws_lb_target_group.gateway[0].arn
      container_name   = "gateway"
      container_port   = 8443
    }
  }
  # Sessions own desired counts. Terraform neither restarts paused tasks nor stops an active session.
  lifecycle {
    ignore_changes = [desired_count]
  }
  depends_on = [
    aws_route.internet,
    aws_route_table_association.public,
    aws_route_table_association.public_b,
    aws_iam_role_policy.execution,
    aws_iam_role_policy.transport,
    aws_lb_listener.gateway
  ]
}
