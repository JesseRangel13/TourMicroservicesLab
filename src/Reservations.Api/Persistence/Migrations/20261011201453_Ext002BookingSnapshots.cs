using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ext002BookingSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TourName",
                schema: "reservations",
                table: "Reservations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "Tour name unavailable (legacy)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TourName",
                schema: "reservations",
                table: "Reservations");
        }
    }
}
