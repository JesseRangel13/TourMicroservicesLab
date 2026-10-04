using Gateway.Web.Identity;
using Catalog.Api.Persistence;
using Reservations.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace Lab.Provisioner;

public static class DatabaseBootstrap
{
    public static async Task RunAsync(ProvisioningSettings settings, string certDir, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(settings.AdminConnection);
        await admin.OpenAsync(ct);
        await ExecuteAsync(admin, "REVOKE ALL ON DATABASE tourlab FROM PUBLIC; REVOKE ALL ON SCHEMA public FROM PUBLIC;", ct);
        foreach (var schema in LocalConfiguration.Schemas)
        {
            foreach (var (role, password) in new[] { ($"{schema}_runtime", settings.RuntimePasswords[schema]), ($"{schema}_migrator", settings.MigrationPasswords[schema]) })
            {
                await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@role)", admin);
                exists.Parameters.AddWithValue("role", role);
                if (await exists.ExecuteScalarAsync(ct) is false)
                    await ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password.Replace("'", "''")}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;", ct);
                await ExecuteAsync(admin, $"GRANT CONNECT ON DATABASE tourlab TO {role}; ALTER ROLE {role} SET search_path TO {schema};", ct);
            }
            await ExecuteAsync(admin, $"""
                CREATE SCHEMA IF NOT EXISTS {schema} AUTHORIZATION {schema}_migrator;
                REVOKE ALL ON SCHEMA {schema} FROM PUBLIC;
                GRANT USAGE ON SCHEMA {schema} TO {schema}_runtime;
                ALTER DEFAULT PRIVILEGES FOR ROLE {schema}_migrator IN SCHEMA {schema}
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {schema}_runtime;
                ALTER DEFAULT PRIVILEGES FOR ROLE {schema}_migrator IN SCHEMA {schema}
                    GRANT USAGE, SELECT ON SEQUENCES TO {schema}_runtime;
                """, ct);
            await using var migrator = new NpgsqlConnection(LocalConfiguration.Connection("localhost", $"{schema}_migrator", settings.MigrationPasswords[schema], certDir));
            await migrator.OpenAsync(ct);
            await ExecuteAsync(migrator, $"""
                CREATE TABLE IF NOT EXISTS {schema}."SchemaVersion" ("Version" integer PRIMARY KEY);
                INSERT INTO {schema}."SchemaVersion" VALUES (1) ON CONFLICT DO NOTHING;
                """, ct);
        }
        var migrationServices = new ServiceCollection();
        migrationServices.AddLogging(o => o.SetMinimumLevel(LogLevel.Warning));
        migrationServices.AddLabIdentity(LocalConfiguration.Connection("localhost", "identity_migrator", settings.MigrationPasswords["identity"], certDir));
        await using (var provider = migrationServices.BuildServiceProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IdentityDb>().Database.MigrateAsync(ct);
        }
        var runtimeServices = new ServiceCollection();
        await ExecuteAsync(admin, "REVOKE ALL ON identity.\"__EFMigrationsHistory\" FROM identity_runtime;", ct);
        runtimeServices.AddLogging(o => o.SetMinimumLevel(LogLevel.Warning));
        runtimeServices.AddLabIdentity(LocalConfiguration.Connection("localhost", "identity_runtime", settings.RuntimePasswords["identity"], certDir));
        await using (var provider = runtimeServices.BuildServiceProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<LabUser>>();
            foreach (var role in new[] { "Tourist", "Admin" })
                if (!await roles.RoleExistsAsync(role)) Ensure(await roles.CreateAsync(new IdentityRole(role)));
            foreach (var seed in settings.Users)
            {
                ct.ThrowIfCancellationRequested();
                if (seed.Role is not ("Tourist" or "Admin")) throw new InvalidOperationException("Seed role must be Tourist or Admin.");
                var user = await users.FindByNameAsync(seed.Name);
                if (user is null)
                {
                    user = new LabUser { Id = Guid.NewGuid().ToString(), UserName = seed.Name, Email = seed.Email, EmailConfirmed = true };
                    Ensure(await users.CreateAsync(user, seed.Password));
                }
                if (!await users.IsInRoleAsync(user, seed.Role)) Ensure(await users.AddToRoleAsync(user, seed.Role));
            }
        }
        var catalogMigration = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(
            LocalConfiguration.Connection("localhost", "catalog_migrator", settings.MigrationPasswords["catalog"], certDir),
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "catalog")).Options;
        await using (var db = new CatalogDb(catalogMigration)) await db.Database.MigrateAsync(ct);
        await ExecuteAsync(admin, "REVOKE ALL ON catalog.\"__EFMigrationsHistory\" FROM catalog_runtime;", ct);
        var catalogRuntime = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(
            LocalConfiguration.Connection("localhost", "catalog_runtime", settings.RuntimePasswords["catalog"], certDir)).Options;
        await using (var db = new CatalogDb(catalogRuntime)) await CatalogSeed.RunAsync(db, ct);
        var reservationsMigration = new DbContextOptionsBuilder<ReservationsDb>().UseNpgsql(
            LocalConfiguration.Connection("localhost", "reservations_migrator", settings.MigrationPasswords["reservations"], certDir),
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")).Options;
        await using (var db = new ReservationsDb(reservationsMigration)) await db.Database.MigrateAsync(ct);
        await ExecuteAsync(admin, "REVOKE ALL ON reservations.\"__EFMigrationsHistory\" FROM reservations_runtime;", ct);
        var paymentsMigration = new DbContextOptionsBuilder<Payments.Api.Persistence.PaymentsDb>().UseNpgsql(
            LocalConfiguration.Connection("localhost", "payments_migrator", settings.MigrationPasswords["payments"], certDir),
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "payments")).Options;
        await using (var db = new Payments.Api.Persistence.PaymentsDb(paymentsMigration)) await db.Database.MigrateAsync(ct);
        var notificationsMigration = new DbContextOptionsBuilder<Notifications.Api.Persistence.NotificationsDb>().UseNpgsql(
            LocalConfiguration.Connection("localhost", "notifications_migrator", settings.MigrationPasswords["notifications"], certDir),
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "notifications")).Options;
        await using (var db = new Notifications.Api.Persistence.NotificationsDb(notificationsMigration)) await db.Database.MigrateAsync(ct);
        await ExecuteAsync(admin, "REVOKE ALL ON payments.\"__EFMigrationsHistory\" FROM payments_runtime; REVOKE ALL ON notifications.\"__EFMigrationsHistory\" FROM notifications_runtime;", ct);
        Console.WriteLine("All five owned schemas migrated and samples seeded without resetting existing data.");
    }
    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Identity operation failed: " + string.Join(",", result.Errors.Select(e => e.Code)));
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
