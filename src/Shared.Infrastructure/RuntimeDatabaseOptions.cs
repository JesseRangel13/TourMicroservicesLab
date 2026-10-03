using Npgsql;

namespace Shared.Infrastructure;

public sealed class RuntimeDatabaseOptions
{
    public string Runtime { get; set; } = "";
    public bool IsValid()
    {
        try
        {
            var value = new NpgsqlConnectionStringBuilder(Runtime);
            return !string.IsNullOrWhiteSpace(value.Host) && value.Username?.EndsWith("_runtime", StringComparison.Ordinal) == true
                && value.MaxPoolSize is >= 1 and <= 5 && value.SslMode == SslMode.VerifyFull
                && !string.IsNullOrWhiteSpace(value.RootCertificate);
        }
        catch (ArgumentException) { return false; }
    }
}
