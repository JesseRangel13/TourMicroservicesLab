using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Lab005Recovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancellationRequested",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PaymentRequested",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PaymentResolved",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            // Preserve accepted LAB-004 operations and known outcomes without cross-service reads.
            migrationBuilder.Sql("""
                UPDATE reservations."Sagas" s SET "PaymentRequested"=EXISTS
                (SELECT 1 FROM reservations."Outbox" o WHERE o."EffectKey"='saga/'||s."Id"::text||'/payment'),
                "PaymentResolved"=(s."ProviderReference" IS NOT NULL OR s."State" IN ('Compensating','Failed')
                AND EXISTS (SELECT 1 FROM reservations."Outbox" o WHERE o."EffectKey"='saga/'||s."Id"::text||'/payment'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationRequested",
                schema: "reservations",
                table: "Sagas");

            migrationBuilder.DropColumn(
                name: "PaymentRequested",
                schema: "reservations",
                table: "Sagas");

            migrationBuilder.DropColumn(
                name: "PaymentResolved",
                schema: "reservations",
                table: "Sagas");
        }
    }
}

