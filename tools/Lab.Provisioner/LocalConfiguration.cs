using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Npgsql;
namespace Lab.Provisioner;

public static class LocalConfiguration
{
    public static readonly string[] Schemas = ["catalog", "reservations", "payments", "notifications", "identity"];
    public static void Generate(string root)
    {
        var directory = Path.Combine(root, ".local");
        if (File.Exists(Path.Combine(directory, "provisioner.json")))
            throw new InvalidOperationException("Private configuration already exists; reuse it. Rotation is separate.");
        Directory.CreateDirectory(directory);
        var certDir = Path.Combine(directory, "certificates");
        Directory.CreateDirectory(certDir);
        using var caKey = RSA.Create(3072);
        var now = DateTimeOffset.UtcNow;
        var caRequest = new CertificateRequest("CN=TourMicroservicesLab Local CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));
        File.WriteAllText(Path.Combine(certDir, "ca.crt"), ca.ExportCertificatePem());
        File.WriteAllText(Path.Combine(certDir, "ca.key"), caKey.ExportPkcs8PrivateKeyPem());
        foreach (var name in new[] { "gateway", "catalog", "reservations", "payments", "notifications", "postgres", "protection" })
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}.tourlab.internal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost"); san.AddDnsName(name); san.AddDnsName($"{name}.tourlab.internal");
            if (name == "gateway") san.AddDnsName("lab.tours.test");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var leaf = request.Create(ca, now.AddHours(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(16));
            using var signed = leaf.CopyWithPrivateKey(key);
            File.WriteAllBytes(Path.Combine(certDir, $"{name}.pfx"), signed.Export(X509ContentType.Pfx));
            File.WriteAllText(Path.Combine(certDir, $"{name}.crt"), signed.ExportCertificatePem());
            File.WriteAllText(Path.Combine(certDir, $"{name}.key"), key.ExportPkcs8PrivateKeyPem());
        }
        using var jwt = RSA.Create(3072);
        File.WriteAllText(Path.Combine(certDir, "jwt.key"), jwt.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(certDir, "jwt.pub"), jwt.ExportSubjectPublicKeyInfoPem());
        var passwords = Schemas.ToDictionary(s => s, _ => NewPassword());
        var migrationPasswords = Schemas.ToDictionary(s => s, _ => NewPassword());
        var adminPassword = NewPassword();
        File.WriteAllText(Path.Combine(directory, "postgres-password"), adminPassword);
        var seedUsers = new[] { "Alice", "Bob", "Admin" }.Select(n => new SeedUser(n, $"{n.ToLowerInvariant()}@tourlab.test", NewPassword(), n == "Admin" ? "Admin" : "Tourist")).ToArray();
        WriteJson(Path.Combine(directory, "provisioner.json"), new ProvisioningSettings(Connection("localhost", "postgres", adminPassword, certDir), passwords, migrationPasswords, seedUsers));
        foreach (var schema in Schemas)
        {
            var service = schema == "identity" ? "gateway" : schema;
            var port = schema == "identity" ? 8443 : 8444 + Array.IndexOf(Schemas, schema);
            WriteJson(Path.Combine(directory, $"{service}.host.json"), RuntimeConfig(service, schema, "localhost", passwords[schema], certDir, port, false));
            WriteJson(Path.Combine(directory, $"{service}.container.json"), RuntimeConfig(service, schema, "postgres", passwords[schema], "/run/lab", 8443, true));
        }
        Console.WriteLine("Private configuration generated under .local. Passwords were not printed. Protect its ACL before use.");
    }
    public static string Connection(string host, string role, string password, string certificates) => new NpgsqlConnectionStringBuilder
    {
        Host = host, Port = host == "localhost" ? 54329 : 5432, Database = "tourlab", Username = role,
        Password = password, MaxPoolSize = 5, Timeout = 5, CommandTimeout = 5,
        SslMode = SslMode.VerifyFull, RootCertificate = CertificatePath(certificates, "ca.crt")
    }.ConnectionString;
    private static object RuntimeConfig(string service, string schema, string dbHost, string password, string certificates, int port, bool container)
    {
        var config = new Dictionary<string, object>
        {
            ["ConnectionStrings"] = new { Runtime = Connection(dbHost, $"{schema}_runtime", password, certificates) },
            ["Jwt"] = new Dictionary<string, string> { ["PublicKeyPath"] = CertificatePath(certificates, "jwt.pub") },
            ["Kestrel"] = new { Endpoints = new { Https = new { Url = $"https://{(container ? "0.0.0.0" : "localhost")}:{port}", Certificate = new { Path = CertificatePath(certificates, $"{service}.pfx") } } } },
            ["AllowedHosts"] = $"localhost;127.0.0.1;lab.tours.test;{service};{service}.tourlab.internal",
            ["LabFeaturesEnabled"] = false
        };
        if (service == "gateway")
        {
            ((Dictionary<string, string>)config["Jwt"])["PrivateKeyPath"] = CertificatePath(certificates, "jwt.key");
            config["DataProtection"] = new { CertificatePath = CertificatePath(certificates, "protection.pfx") };
            var routes = new Dictionary<string, object>(); var clusters = new Dictionary<string, object>();
            for (var i = 0; i < 4; i++)
            {
                var destination = Schemas[i];
                routes[destination] = new { ClusterId = destination, AuthorizationPolicy = "Api", Match = new { Path = $"/api/{destination}/v1/{{**rest}}" },
                    Transforms = new[] { new { PathRemovePrefix = $"/api/{destination}" } } };
                clusters[destination] = new { Destinations = new { primary = new { Address = container ? $"https://{destination}:8443/" : $"https://localhost:{8444 + i}/" } },
                    HttpClient = new { MaxConnectionsPerServer = 10 }, HttpRequest = new { ActivityTimeout = "00:00:10" } };
            }
            config["ReverseProxy"] = new { Routes = routes, Clusters = clusters };
        }
        return config;
    }
    private static string NewPassword() => "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private static string CertificatePath(string directory, string file) => directory.StartsWith('/') ? $"{directory}/{file}" : Path.Combine(directory, file);
    public static void RefreshRuntimeConfiguration(string root)
    {
        var directory = Path.Combine(root, ".local");
        var settings = JsonSerializer.Deserialize<ProvisioningSettings>(File.ReadAllText(Path.Combine(directory, "provisioner.json")))
            ?? throw new InvalidOperationException("Private settings missing.");
        foreach (var schema in Schemas)
        {
            var service = schema == "identity" ? "gateway" : schema;
            var port = schema == "identity" ? 8443 : 8444 + Array.IndexOf(Schemas, schema);
            WriteJson(Path.Combine(directory, $"{service}.host.json"), RuntimeConfig(service, schema, "localhost", settings.RuntimePasswords[schema], Path.Combine(directory, "certificates"), port, false));
            WriteJson(Path.Combine(directory, $"{service}.container.json"), RuntimeConfig(service, schema, "postgres", settings.RuntimePasswords[schema], "/run/lab", 8443, true));
        }
        Console.WriteLine("Runtime configuration refreshed; certificates, passwords, and database data retained.");
    }
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
public sealed record SeedUser(string Name, string Email, string Password, string Role);
public sealed record ProvisioningSettings(string AdminConnection, Dictionary<string, string> RuntimePasswords,
    Dictionary<string, string> MigrationPasswords, SeedUser[] Users);
