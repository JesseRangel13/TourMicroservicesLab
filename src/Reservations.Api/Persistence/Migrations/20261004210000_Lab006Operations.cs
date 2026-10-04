using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Reservations.Api.Persistence;
namespace Reservations.Api.Migrations;
[DbContext(typeof(ReservationsDb))]
[Migration("20261004210000_Lab006Operations")]
public sealed class Lab006Operations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE reservations."DlqInspections" (
                "DeliveryId" uuid PRIMARY KEY, "Body" text NOT NULL, "Receipt" text NOT NULL,
                "ExpiresAtUtc" timestamptz NOT NULL, "ReplayUntilUtc" timestamptz NULL,
                "Replaying" boolean NOT NULL DEFAULT false, "Finished" boolean NOT NULL DEFAULT false);
            CREATE TABLE reservations."OperationsAudit" (
                "Id" uuid PRIMARY KEY, "Actor" varchar(128) NOT NULL, "Action" varchar(100) NOT NULL,
                "DeliveryId" uuid NULL, "OccurredAtUtc" timestamptz NOT NULL);
            CREATE INDEX "IX_OperationsAudit_Time" ON reservations."OperationsAudit" ("OccurredAtUtc");
            """);
        migrationBuilder.Sql("CREATE TABLE reservations.\"Faults\" (\"Id\" integer PRIMARY KEY CHECK (\"Id\"=1), \"Mode\" varchar(40) NOT NULL, \"Remaining\" integer NOT NULL CHECK (\"Remaining\" BETWEEN 0 AND 100)); INSERT INTO reservations.\"Faults\" VALUES (1,'None',0);");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    { throw new NotSupportedException("Operational evidence must be retained; this lab migration is forward-only."); }
}