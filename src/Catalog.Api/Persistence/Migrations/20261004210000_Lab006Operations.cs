using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Catalog.Api.Persistence;
namespace Catalog.Api.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("20261004210000_Lab006Operations")]
public sealed class Lab006Operations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE catalog."DlqInspections" (
                "DeliveryId" uuid PRIMARY KEY, "Body" text NOT NULL, "Receipt" text NOT NULL,
                "ExpiresAtUtc" timestamptz NOT NULL, "ReplayUntilUtc" timestamptz NULL,
                "Replaying" boolean NOT NULL DEFAULT false, "Finished" boolean NOT NULL DEFAULT false);
            CREATE TABLE catalog."OperationsAudit" (
                "Id" uuid PRIMARY KEY, "Actor" varchar(128) NOT NULL, "Action" varchar(100) NOT NULL,
                "DeliveryId" uuid NULL, "OccurredAtUtc" timestamptz NOT NULL);
            CREATE INDEX "IX_OperationsAudit_Time" ON catalog."OperationsAudit" ("OccurredAtUtc");
            """);
        
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    { throw new NotSupportedException("Operational evidence must be retained; this lab migration is forward-only."); }
}