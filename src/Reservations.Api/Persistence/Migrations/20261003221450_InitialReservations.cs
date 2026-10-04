using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reservations");

            migrationBuilder.CreateTable(
                name: "Inbox",
                schema: "reservations",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inbox", x => new { x.ConsumerName, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "reservations",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EnvelopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.DeliveryId);
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                schema: "reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Participants = table.Column<int>(type: "integer", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    TotalAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                    table.CheckConstraint("CK_Reservation", "\"Participants\" BETWEEN 1 AND 6 AND \"UnitAmountMinor\" > 0 AND \"TotalAmountMinor\"::numeric = \"UnitAmountMinor\"::numeric * \"Participants\" AND \"Currency\"='MXN' AND \"Version\">0");
                });

            migrationBuilder.CreateTable(
                name: "TourProjections",
                schema: "reservations",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    PriceVersion = table.Column<long>(type: "bigint", nullable: false),
                    UnitAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TourProjections", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRequests",
                schema: "reservations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OperationName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseStatus = table.Column<int>(type: "integer", nullable: false),
                    ResponseBody = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRequests", x => new { x.UserId, x.OperationName, x.Key });
                    table.ForeignKey(
                        name: "FK_IdempotencyRequests_Reservations_ResourceId",
                        column: x => x.ResourceId,
                        principalSchema: "reservations",
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sagas",
                schema: "reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefundOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HoldExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sagas", x => x.Id);
                    table.CheckConstraint("CK_Saga", "\"Version\">0");
                    table.ForeignKey(
                        name: "FK_Sagas_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalSchema: "reservations",
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "History",
                schema: "reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    To = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                    table.ForeignKey(
                        name: "FK_History_Sagas_SagaId",
                        column: x => x.SagaId,
                        principalSchema: "reservations",
                        principalTable: "Sagas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_History_SagaId_MessageId",
                schema: "reservations",
                table: "History",
                columns: new[] { "SagaId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_History_SagaId_OccurredAtUtc",
                schema: "reservations",
                table: "History",
                columns: new[] { "SagaId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRequests_ResourceId",
                schema: "reservations",
                table: "IdempotencyRequests",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_ProcessedAtUtc",
                schema: "reservations",
                table: "Inbox",
                column: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Destination_EffectKey",
                schema: "reservations",
                table: "Outbox",
                columns: new[] { "Destination", "EffectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_PublishedAtUtc_OccurredAtUtc",
                schema: "reservations",
                table: "Outbox",
                columns: new[] { "PublishedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_Status",
                schema: "reservations",
                table: "Reservations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_UserId_CreatedAtUtc",
                schema: "reservations",
                table: "Reservations",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sagas_HoldId",
                schema: "reservations",
                table: "Sagas",
                column: "HoldId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sagas_PaymentOperationId",
                schema: "reservations",
                table: "Sagas",
                column: "PaymentOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sagas_RefundOperationId",
                schema: "reservations",
                table: "Sagas",
                column: "RefundOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sagas_ReservationId",
                schema: "reservations",
                table: "Sagas",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sagas_State_DeadlineUtc",
                schema: "reservations",
                table: "Sagas",
                columns: new[] { "State", "DeadlineUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "History",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "IdempotencyRequests",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "Inbox",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "TourProjections",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "Sagas",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "Reservations",
                schema: "reservations");
        }
    }
}
