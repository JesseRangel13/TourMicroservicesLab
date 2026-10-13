# Educational extensions reuse Catalog/Reservations tasks. No deployment in this task.
resource "aws_s3_bucket" "tour_images" {
  bucket        = "${var.prefix}-${var.account_id}-images"
  force_destroy = false
}
resource "aws_s3_bucket_public_access_block" "tour_images" {
  bucket                  = aws_s3_bucket.tour_images.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
resource "aws_s3_bucket_ownership_controls" "tour_images" {
  bucket = aws_s3_bucket.tour_images.id
  rule { object_ownership = "BucketOwnerEnforced" }
}
resource "aws_s3_bucket_server_side_encryption_configuration" "tour_images" {
  bucket = aws_s3_bucket.tour_images.id
  rule {
    apply_server_side_encryption_by_default { sse_algorithm = "AES256" }
  }
}
resource "aws_s3_bucket_policy" "tour_images" {
  bucket = aws_s3_bucket.tour_images.id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{ Sid = "DenyNonTLS", Effect = "Deny", Principal = "*", Action = "s3:*",
  Resource = [aws_s3_bucket.tour_images.arn, "${aws_s3_bucket.tour_images.arn}/*"], Condition = { Bool = { "aws:SecureTransport" = "false" } } }] })
}
resource "aws_dynamodb_table" "bookings" {
  name                        = "${var.prefix}-bookings"
  billing_mode                = "PAY_PER_REQUEST"
  hash_key                    = "pk"
  range_key                   = "sk"
  deletion_protection_enabled = true
  attribute {
    name = "pk"
    type = "S"
  }
  attribute {
    name = "sk"
    type = "S"
  }
  attribute {
    name = "created"
    type = "S"
  }
  global_secondary_index {
    name            = "UserBookings"
    projection_type = "ALL"
    key_schema {
      attribute_name = "pk"
      key_type       = "HASH"
    }
    key_schema {
      attribute_name = "created"
      key_type       = "RANGE"
    }
  }
  # false selects the default AWS-owned encryption key; data remains encrypted without extra KMS key usage charges.
  server_side_encryption { enabled = false }
  lifecycle { prevent_destroy = true }
}
resource "aws_sqs_queue" "booking_projection_dlq" {
  name                      = "${var.prefix}-booking-projection-dlq"
  fifo_queue                = false
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}
resource "aws_sqs_queue" "booking_projection" {
  name                       = "${var.prefix}-booking-projection"
  fifo_queue                 = false
  message_retention_seconds  = 1209600
  visibility_timeout_seconds = 60
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy             = jsonencode({ deadLetterTargetArn = aws_sqs_queue.booking_projection_dlq.arn, maxReceiveCount = 5 })
}
resource "aws_sqs_queue_redrive_allow_policy" "booking_projection" {
  queue_url            = aws_sqs_queue.booking_projection_dlq.url
  redrive_allow_policy = jsonencode({ redrivePermission = "byQueue", sourceQueueArns = [aws_sqs_queue.booking_projection.arn] })
}
resource "aws_iam_role_policy" "catalog_images" {
  name = "${var.prefix}-images"
  role = aws_iam_role.application["catalog"].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Action = ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"],
  Resource = "${aws_s3_bucket.tour_images.arn}/tours/*" }] })
}
resource "aws_iam_role_policy" "booking_projection" {
  name = "${var.prefix}-booking-projection"
  role = aws_iam_role.application["reservations"].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    { Effect = "Allow", Action = ["dynamodb:PutItem", "dynamodb:GetItem"], Resource = aws_dynamodb_table.bookings.arn },
    { Effect = "Allow", Action = ["dynamodb:Query"], Resource = "${aws_dynamodb_table.bookings.arn}/index/UserBookings" },
    { Effect = "Allow", Action = ["sqs:SendMessage", "sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes", "sqs:GetQueueUrl"], Resource = aws_sqs_queue.booking_projection.arn },
    { Effect = "Allow", Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes", "sqs:GetQueueUrl"], Resource = aws_sqs_queue.booking_projection_dlq.arn }
  ] })
}
output "extensions" {
  value = { image_bucket = aws_s3_bucket.tour_images.id, booking_table = aws_dynamodb_table.bookings.name,
  projection_queue_url = aws_sqs_queue.booking_projection.url, projection_dlq_url = aws_sqs_queue.booking_projection_dlq.url }
}
