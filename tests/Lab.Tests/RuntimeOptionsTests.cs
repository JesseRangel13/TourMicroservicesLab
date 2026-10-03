using Shared.Infrastructure;

namespace Lab.Tests;
public sealed class RuntimeOptionsTests
{
    [Theory]
    [InlineData("catalog_runtime", 5, "VerifyFull", true)]
    [InlineData("catalog_migrator", 5, "VerifyFull", false)]
    [InlineData("postgres", 5, "VerifyFull", false)]
    [InlineData("catalog_runtime", 100, "VerifyFull", false)]
    [InlineData("catalog_runtime", 5, "Require", false)]
    public void RuntimeConfigurationRejectsPrivilegedOrUnboundedConnections(string role, int pool, string tls, bool expected)
    {
        var options = new RuntimeDatabaseOptions { Runtime = $"Host=localhost;Username={role};Maximum Pool Size={pool};SSL Mode={tls};Root Certificate=ca.crt" };
        Assert.Equal(expected, options.IsValid());
    }
}
