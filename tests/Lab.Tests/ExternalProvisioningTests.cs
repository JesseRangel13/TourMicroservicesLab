using Lab.Provisioner;
using Npgsql;

namespace Lab.Tests;

public sealed class ExternalProvisioningTests
{
    private static ProvisioningSettings Settings()
    {
        var passwords = LocalConfiguration.Schemas.ToDictionary(s => s, _ => "fictional-test-password");
        string Connection(string role, string host) => new NpgsqlConnectionStringBuilder
        { Host = host, Database = "tourlab", Username = role, Password = "fictional-test-password", SslMode = SslMode.VerifyFull, MaxPoolSize = 5 }.ConnectionString;
        return new(Connection("owner", "ep-study.example.neon.tech"), passwords, passwords, [],
            LocalConfiguration.Schemas.ToDictionary(s => s, s => Connection(s + "_runtime", "ep-study-pooler.example.neon.tech")),
            LocalConfiguration.Schemas.ToDictionary(s => s, s => Connection(s + "_migrator", "ep-study.example.neon.tech")));
    }

    [Fact]
    public void ExplicitDirectMigratorsAndPooledRuntimeRolesAreValidWithoutConnecting() => ExternalProvisioning.Validate(Settings());

    [Theory]
    [InlineData("role")][InlineData("tls")][InlineData("pool")][InlineData("host")]
    public void UnsafeExternalSettingsAreRejectedBeforeConnection(string fault)
    {
        var settings = Settings(); var value = new NpgsqlConnectionStringBuilder(settings.RuntimeConnections!["catalog"]);
        switch (fault)
        {
            case "role": value.Username = "owner"; break;
            case "tls": value.SslMode = SslMode.Require; break;
            case "pool": value.MaxPoolSize = 100; break;
            case "host": value.Host = "ep-unrelated.example.neon.tech"; break;
        }
        settings.RuntimeConnections["catalog"] = value.ConnectionString;
        Assert.Throws<InvalidOperationException>(() => ExternalProvisioning.Validate(settings));
    }
}
