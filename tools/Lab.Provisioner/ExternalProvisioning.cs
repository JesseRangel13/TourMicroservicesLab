using Npgsql;

namespace Lab.Provisioner;

public static class ExternalProvisioning
{
    public static void Validate(ProvisioningSettings settings)
    {
        var admin = new NpgsqlConnectionStringBuilder(settings.AdminConnection);
        if (admin.Database != "tourlab" || admin.SslMode != SslMode.VerifyFull || admin.Host is null || !admin.Host.EndsWith(".neon.tech", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected the explicit Neon study database with VerifyFull TLS.");
        foreach (var migration in new[] { false, true })
        {
            var connections = migration ? settings.MigrationConnections : settings.RuntimeConnections;
            if (connections is null || !connections.Keys.Order().SequenceEqual(LocalConfiguration.Schemas.Order()))
                throw new InvalidOperationException("All five explicit runtime and direct migration connections are required.");
            foreach (var schema in LocalConfiguration.Schemas)
            {
                var value = new NpgsqlConnectionStringBuilder(connections[schema]);
                var role = schema + (migration ? "_migrator" : "_runtime");
                var password = (migration ? settings.MigrationPasswords : settings.RuntimePasswords)[schema];
                if (value.Username != role || value.Password != password || value.Database != "tourlab" || value.SslMode != SslMode.VerifyFull
                    || value.MaxPoolSize is < 1 or > 5 || value.Host is null || !value.Host.EndsWith(".neon.tech", StringComparison.OrdinalIgnoreCase)
                    || !migration && value.Host.Replace("-pooler", "", StringComparison.OrdinalIgnoreCase) != admin.Host
                    || migration && (value.Host != admin.Host || value.Host.Contains("-pooler", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Invalid isolated external connection; migration must use the direct study endpoint.");
            }
        }
    }
}
