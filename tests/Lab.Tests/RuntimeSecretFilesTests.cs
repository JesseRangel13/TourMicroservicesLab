using Shared.Infrastructure;
using Npgsql;

namespace Lab.Tests;

public sealed class RuntimeSecretFilesTests
{
    [Theory]
    [InlineData(true, "VerifyFull", 5, true)]
    [InlineData(false, "VerifyFull", 5, false)]
    [InlineData(true, "Require", 5, false)]
    [InlineData(true, "VerifyFull", 100, false)]
    public void PublicRootTrustNeverBypassesHostnameTlsOrPoolLimits(bool systemTrust, string ssl, int pool, bool expected)
    {
        var value = new RuntimeDatabaseOptions { Runtime = $"Host=example.neon.tech;Username=catalog_runtime;SSL Mode={ssl};Maximum Pool Size={pool}", TrustSystemRootCertificates = systemTrust };
        Assert.Equal(expected, value.IsValid());
    }

    [Fact]
    public void DataProtectionPemHasPrivateKeyAndSupportsEncryption()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=TestOnly", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var directory = Path.Combine(Path.GetTempPath(), "tourlab-cert-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var certPath = Path.Combine(directory, "cert.crt"); var keyPath = Path.Combine(directory, "key.key");
            File.WriteAllText(certPath, certificate.ExportCertificatePem()); File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            using var loaded = RuntimeSecretFiles.ProtectionCertificate(certPath, keyPath);
            Assert.True(loaded.HasPrivateKey);
            using var publicKey = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(loaded)!;
            using var privateKey = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(loaded)!;
            Assert.Equal(new byte[] { 1, 2, 3 }, privateKey.Decrypt(publicKey.Encrypt([1, 2, 3], System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256), System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256));
        }
        finally
        {
            File.Delete(Path.Combine(directory, "cert.crt")); File.Delete(Path.Combine(directory, "key.key"));
            Directory.Delete(directory); // only the empty directory created by this test
        }
    }
}
