resource "aws_vpc" "lab" {
  cidr_block           = "10.78.0.0/16"
  enable_dns_support   = true
  enable_dns_hostnames = true
  tags                 = { Name = var.prefix }
}
resource "aws_subnet" "public" {
  vpc_id                  = aws_vpc.lab.id
  cidr_block              = "10.78.1.0/24"
  availability_zone       = "${var.region}a"
  map_public_ip_on_launch = false
  tags                    = { Name = "${var.prefix}-public" }
}
resource "aws_internet_gateway" "lab" {
  vpc_id = aws_vpc.lab.id
}
resource "aws_route_table" "public" {
  vpc_id = aws_vpc.lab.id
}
resource "aws_route" "internet" {
  route_table_id         = aws_route_table.public.id
  destination_cidr_block = "0.0.0.0/0"
  gateway_id             = aws_internet_gateway.lab.id
}
resource "aws_route_table_association" "public" {
  subnet_id      = aws_subnet.public.id
  route_table_id = aws_route_table.public.id
}
resource "aws_subnet" "public_b" {
  vpc_id                  = aws_vpc.lab.id
  cidr_block              = "10.78.2.0/24"
  availability_zone       = "${var.region}b"
  map_public_ip_on_launch = false
  tags                    = { Name = "${var.prefix}-public-b" }
}
resource "aws_route_table_association" "public_b" {
  subnet_id      = aws_subnet.public_b.id
  route_table_id = aws_route_table.public.id
}
resource "aws_security_group" "alb" {
  name        = "${var.prefix}-alb"
  description = "Study ALB; ingress restricted to authorized client CIDR"
  vpc_id      = aws_vpc.lab.id
  tags        = { Name = "${var.prefix}-alb" }
}
resource "aws_vpc_security_group_ingress_rule" "alb_http" {
  security_group_id = aws_security_group.alb.id
  cidr_ipv4         = var.client_cidr
  from_port         = 80
  to_port           = 80
  ip_protocol       = "tcp"
}
resource "aws_vpc_security_group_egress_rule" "alb_to_gateway" {
  security_group_id            = aws_security_group.alb.id
  referenced_security_group_id = aws_security_group.service["gateway"].id
  from_port                    = 8443
  to_port                      = 8443
  ip_protocol                  = "tcp"
}
resource "aws_security_group" "service" {
  for_each    = local.services
  name        = "${var.prefix}-${each.key}"
  description = "Owned study service; ingress via explicit groups only"
  vpc_id      = aws_vpc.lab.id
}
resource "aws_vpc_security_group_ingress_rule" "client" {
  security_group_id = aws_security_group.service["gateway"].id
  cidr_ipv4         = var.client_cidr
  from_port         = 8443
  to_port           = 8443
  ip_protocol       = "tcp"
}
resource "aws_vpc_security_group_ingress_rule" "alb_forward" {
  security_group_id            = aws_security_group.service["gateway"].id
  referenced_security_group_id = aws_security_group.alb.id
  from_port                    = 8443
  to_port                      = 8443
  ip_protocol                  = "tcp"
}
resource "aws_vpc_security_group_ingress_rule" "gateway" {
  for_each                     = local.business
  security_group_id            = aws_security_group.service[each.key].id
  referenced_security_group_id = aws_security_group.service["gateway"].id
  from_port                    = 8443
  to_port                      = 8443
  ip_protocol                  = "tcp"
}
resource "aws_vpc_security_group_ingress_rule" "quote" {
  security_group_id            = aws_security_group.service["catalog"].id
  referenced_security_group_id = aws_security_group.service["reservations"].id
  from_port                    = 8443
  to_port                      = 8443
  ip_protocol                  = "tcp"
}
resource "aws_vpc_security_group_egress_rule" "https" {
  for_each          = local.services
  security_group_id = aws_security_group.service[each.key].id
  cidr_ipv4         = "0.0.0.0/0"
  from_port         = 443
  to_port           = 443
  ip_protocol       = "tcp"
}
resource "aws_vpc_security_group_egress_rule" "database" {
  for_each          = local.services
  security_group_id = aws_security_group.service[each.key].id
  cidr_ipv4         = "0.0.0.0/0"
  from_port         = 5432
  to_port           = 5432
  ip_protocol       = "tcp"
}
resource "aws_vpc_security_group_egress_rule" "gateway" {
  for_each                     = local.business
  security_group_id            = aws_security_group.service["gateway"].id
  referenced_security_group_id = aws_security_group.service[each.key].id
  from_port                    = 8443
  to_port                      = 8443
  ip_protocol                  = "tcp"
}
resource "aws_vpc_security_group_egress_rule" "quote" {
  security_group_id            = aws_security_group.service["reservations"].id
  referenced_security_group_id = aws_security_group.service["catalog"].id
  from_port                    = 8443
  to_port                      = 8443
  ip_protocol                  = "tcp"
}
