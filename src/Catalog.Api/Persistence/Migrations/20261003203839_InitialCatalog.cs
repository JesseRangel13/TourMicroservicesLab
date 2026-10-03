using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "catalog",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EnvelopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.DeliveryId);
                });

            migrationBuilder.CreateTable(
                name: "Tours",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tours", x => x.Id);
                    table.CheckConstraint("CK_Tour", "length(btrim(\"Name\")) > 0 AND \"Version\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TourId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnitAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    AvailableSeats = table.Column<int>(type: "integer", nullable: false),
                    PriceVersion = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.CheckConstraint("CK_Session", "\"UnitAmountMinor\" > 0 AND \"Currency\" = 'MXN' AND \"Capacity\" >= 0 AND \"AvailableSeats\" >= 0 AND \"AvailableSeats\" <= \"Capacity\" AND \"Version\" > 0 AND \"PriceVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_Sessions_Tours_TourId",
                        column: x => x.TourId,
                        principalSchema: "catalog",
                        principalTable: "Tours",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Participants = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UnitAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    TotalAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.CheckConstraint("CK_Quote", "\"Participants\" BETWEEN 1 AND 6 AND \"UnitAmountMinor\" > 0 AND \"TotalAmountMinor\" > 0 AND \"Currency\" = 'MXN' AND length(\"UserId\") > 0 AND \"TotalAmountMinor\"::numeric = \"UnitAmountMinor\"::numeric * \"Participants\"");
                    table.ForeignKey(
                        name: "FK_Quotes_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "catalog",
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Holds",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Participants = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequestFingerprint = table.Column<string>(type: "text", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holds", x => x.Id);
                    table.CheckConstraint("CK_Hold", "\"Version\" > 0 AND \"Status\" IN ('Held','Confirmed','Released','Expired','Rejected') AND ((\"SessionId\" IS NULL AND \"QuoteId\" IS NULL AND \"Participants\" = 0 AND \"Status\" IN ('Released','Rejected')) OR (\"SessionId\" IS NOT NULL AND \"QuoteId\" IS NOT NULL AND \"ReservationId\" IS NOT NULL AND \"UserId\" IS NOT NULL AND \"Participants\" BETWEEN 1 AND 6 AND \"ExpiresAtUtc\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_Holds_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalSchema: "catalog",
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Holds_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "catalog",
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Holds_QuoteId",
                schema: "catalog",
                table: "Holds",
                column: "QuoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holds_SessionId",
                schema: "catalog",
                table: "Holds",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Holds_Status_ExpiresAtUtc",
                schema: "catalog",
                table: "Holds",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Destination_EffectKey",
                schema: "catalog",
                table: "Outbox",
                columns: new[] { "Destination", "EffectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_PublishedAtUtc_OccurredAtUtc",
                schema: "catalog",
                table: "Outbox",
                columns: new[] { "PublishedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_ExpiresAtUtc",
                schema: "catalog",
                table: "Quotes",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_RequestId",
                schema: "catalog",
                table: "Quotes",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_SessionId",
                schema: "catalog",
                table: "Quotes",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TourId_StartsAtUtc",
                schema: "catalog",
                table: "Sessions",
                columns: new[] { "TourId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Tours_Active_Name_Id",
                schema: "catalog",
                table: "Tours",
                columns: new[] { "Active", "Name", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Holds",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Quotes",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Tours",
                schema: "catalog");
        }
    }
}
