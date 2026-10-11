using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Shared.Infrastructure;

// Optional ECS adapter. Local LAB_CONFIG mounts retain their existing behavior.
public static class RuntimeSecretFiles
{
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        ["LAB_CA_PEM"] = "ca.crt", ["LAB_TLS_CERT_PEM"] = "tls.crt", ["LAB_TLS_KEY_PEM"] = "tls.key",
        ["LAB_JWT_PUBLIC_PEM"] = "jwt.pub", ["LAB_JWT_PRIVATE_PEM"] = "jwt.key",
        ["LAB_SERVICE_PUBLIC_PEM"] = "service.pub", ["LAB_SERVICE_PRIVATE_PEM"] = "service.key",
        ["LAB_PROTECTION_CERT_PEM"] = "protection.crt", ["LAB_PROTECTION_KEY_PEM"] = "protection.key"
    };

    public static string? Materialize()
    {
        var json = Environment.GetEnvironmentVariable("LAB_RUNTIME_JSON");
        if (json is null) return null;
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Injected runtime files require Linux.");
        const string root = "/tmp/tourlab";
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (variable, filename) in Files)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (value is null) continue;
            if (Encoding.UTF8.GetByteCount(value) > 4096 || !value.Contains("-----BEGIN", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid injected PEM material.");
            WritePrivate(Path.Combine(root, filename), value);
            Environment.SetEnvironmentVariable(variable, null);
        }
        var ca = File.ReadAllText(Path.Combine(root, "ca.crt"));
        // Keep public roots for Neon/AWS and append the private API CA; no trust bypass.
        WritePrivate(Path.Combine(root, "trust.crt"), File.ReadAllText("/etc/ssl/certs/ca-certificates.crt") + "\n" + ca);
        Environment.SetEnvironmentVariable("SSL_CERT_FILE", Path.Combine(root, "trust.crt"));
        var config = Path.Combine(root, "config.json");
        WritePrivate(config, json);
        Environment.SetEnvironmentVariable("LAB_RUNTIME_JSON", null);
        return config;
    }

    private static void WritePrivate(string path, string value)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Private runtime files require Linux.");
        using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        using (var writer = new StreamWriter(stream)) writer.Write(value);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static X509Certificate2 ProtectionCertificate(string certificatePath, string? keyPath) =>
        string.IsNullOrWhiteSpace(keyPath) ? X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null)
            : X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
}
