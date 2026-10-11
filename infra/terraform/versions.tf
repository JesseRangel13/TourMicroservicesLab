terraform {
  required_version = ">= 1.13.5, < 2.0"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 6.0" }
  }
  backend "local" { path = "../../.local/aws/terraform.tfstate" }
  # Keep local state private; no mandatory paid/remote backend.
}

provider "aws" {
  region              = var.region
  allowed_account_ids = [var.account_id]
  default_tags {
    tags = local.tags
  }
}

locals {
  tags         = { Project = "TourMicroservicesLab", Environment = "study", Prefix = var.prefix }
  services     = toset(["gateway", "catalog", "reservations", "payments", "notifications"])
  business     = toset(["catalog", "reservations", "payments", "notifications"])
  assemblies   = { gateway = "Gateway.Web", catalog = "Catalog.Api", reservations = "Reservations.Api", payments = "Payments.Api", notifications = "Notifications.Api" }
  destinations = { catalog = ["reservations"], reservations = ["catalog", "payments", "notifications"], payments = ["reservations"], notifications = [] }
}
