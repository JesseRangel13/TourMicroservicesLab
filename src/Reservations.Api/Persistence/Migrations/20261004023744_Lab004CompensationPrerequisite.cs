using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Lab004CompensationPrerequisite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderReference",
                schema: "reservations",
                table: "Sagas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RefundCompleted",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RefundRequired",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SeatsReleaseRequired",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SeatsReleased",
                schema: "reservations",
                table: "Sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderReference",
                schema: "reservations",
                table: "Sagas");

            migrationBuilder.DropColumn(
                name: "RefundCompleted",
                schema: "reservations",
                table: "Sagas");

            migrationBuilder.DropColumn(
                name: "RefundRequired",
                schema: "reservations",
                table: "Sagas");

            migrationBuilder.DropColumn(
                name: "SeatsReleaseRequired",
                schema: "reservations",
                table: "Sagas");

            migrationBuilder.DropColumn(
                name: "SeatsReleased",
                schema: "reservations",
                table: "Sagas");
        }
    }
}
