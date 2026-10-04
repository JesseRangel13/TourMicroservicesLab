using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Catalog.Api.Application;
using Catalog.Api.Persistence;
using Lab.Provisioner;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Reservations.Api.Persistence;
using Shared.Contracts;
namespace Lab.Tests;

internal static class ReservationTestSupport
{
    internal static string Connection(string schema) => LocalConfiguration.Connection("localhost", schema + "_runtime", IntegrationTests.Settings.RuntimePasswords[schema], Path.Combine(IntegrationTests.Root, ".local", "certificates"));
    internal static CatalogDb Catalog() => new(new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(Connection("catalog")).Options);
    internal static ReservationsDb Reservations() => new(new DbContextOptionsBuilder<ReservationsDb>().UseNpgsql(Connection("reservations")).Options);
    internal static async Task<SessionView> SessionAsync(int capacity = 10)
    {
        await using var db = Catalog(); var handler = new CatalogCommands(db, TimeProvider.System);
        var tour = await handler.CreateAsync(new("LAB-003 evidence " + Guid.NewGuid(), "Preserved test data."), default);
        return await handler.CreateSessionAsync(tour.Id, new(DateTimeOffset.UtcNow.AddDays(10), 12345, capacity), default);
    }
    internal static async Task<(string Id, string Token)> IdentityAsync(string name)
    {
        await using var connection = new NpgsqlConnection(Connection("identity")); await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT \"Id\" FROM identity.\"AspNetUsers\" WHERE \"UserName\"=@name", connection); query.Parameters.AddWithValue("name", name);
        var id = (string)(await query.ExecuteScalarAsync() ?? throw new InvalidOperationException("Seed user required."));
        using var rsa = RSA.Create(); rsa.ImportFromPem(File.ReadAllText(Path.Combine(IntegrationTests.Root, ".local", "certificates", "jwt.key")));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("tourlab-identity", "tourlab-api", [new Claim("sub", id), new Claim("name", name), new Claim("role", name == "Admin" ? "Admin" : "Tourist")],
            now, now.AddMinutes(15), new SigningCredentials(new RsaSecurityKey(rsa) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } }, SecurityAlgorithms.RsaSha256));
        return (id, new JwtSecurityTokenHandler().WriteToken(token));
    }
}
