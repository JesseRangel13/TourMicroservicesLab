variable "region" {
  type    = string
  default = "us-east-1"
  validation {
    condition     = var.region == "us-east-1"
    error_message = "This priced study profile targets us-east-1."
  }
}
variable "enable_alb_study_profile" {
  type        = bool
  default     = false
  description = "Retained earlier teaching profile; explicit opt-in cost/TLS/circuit review required. Extensions default to direct TLS without ALB."
}
variable "account_id" {
  type = string
  validation {
    condition     = can(regex("^[0-9]{12}$", var.account_id))
    error_message = "Supply the intended study account ID."
  }
}
variable "prefix" {
  type    = string
  default = "tourlab-study"
  validation {
    condition     = can(regex("^tourlab-[a-z0-9-]{2,20}$", var.prefix))
    error_message = "Use a unique tourlab- study prefix."
  }
}
variable "client_cidr" {
  type = string
  validation {
    condition     = can(cidrnetmask(var.client_cidr)) && can(regex("/(2[4-9]|3[0-2])$", var.client_cidr))
    error_message = "Use an authorized IPv4 /24..32; prefer your PC's /32. Unrestricted ingress is forbidden."
  }
}
variable "budget_email" {
  type = string
  validation {
    condition     = can(regex("^[^@ ]+@[^@ ]+\\.[^@ ]+$", var.budget_email))
    error_message = "Supply your actual budget notification email; there is no invented default."
  }
}
variable "monthly_budget_usd" {
  type    = number
  default = 5
  validation {
    condition     = var.monthly_budget_usd > 0 && var.monthly_budget_usd <= 20
    error_message = "Review a study budget between zero and twenty USD."
  }
}
variable "images" {
  description = "Five ECR image references pinned by SHA256 digest; LAB-009 builds/pushes them explicitly. No latest tags."
  type        = map(string)
  validation {
    condition     = toset(keys(var.images)) == toset(["gateway", "catalog", "reservations", "payments", "notifications"]) && alltrue([for v in values(var.images) : can(regex("^[0-9]{12}\\.dkr\\.ecr\\.us-east-1\\.amazonaws\\.com/tourlab-[a-z0-9-]+/[a-z]+@sha256:[0-9a-f]{64}$", v))])
    error_message = "Provide all five digest-pinned study ECR references."
  }
}
variable "secret_arns" {
  description = "Only existing standard SecureString parameter ARNs, never values. Provision outside Terraform."
  type        = map(map(string))
  validation {
    condition     = toset(keys(var.secret_arns)) == toset(["gateway", "catalog", "reservations", "payments", "notifications"]) && alltrue(flatten([for refs in values(var.secret_arns) : [for arn in values(refs) : can(regex("^arn:aws:ssm:us-east-1:[0-9]{12}:parameter/tourlab-[a-z0-9-]+/", arn))]]))
    error_message = "Provide parameter references for exactly five services under the study prefix."
  }
}
variable "task_sizes" {
  description = "Conservative review profile; local resource-capped evidence does not prove Fargate load capacity."
  type        = map(object({ cpu = number, memory = number }))
  default     = { gateway = { cpu = 512, memory = 1024 }, catalog = { cpu = 512, memory = 1024 }, reservations = { cpu = 512, memory = 1024 }, payments = { cpu = 512, memory = 1024 }, notifications = { cpu = 512, memory = 1024 } }
  validation {
    condition     = toset(keys(var.task_sizes)) == toset(["gateway", "catalog", "reservations", "payments", "notifications"]) && alltrue([for s in values(var.task_sizes) : s.cpu == 512 && contains([1024, 2048], s.memory) || s.cpu == 1024 && s.memory == 2048])
    error_message = "Use 0.5 vCPU with 1/2 GB or 1 vCPU with 2 GB and a reviewed cost estimate. Smaller sizes require new measured evidence and a reviewed change."
  }
}
