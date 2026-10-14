using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RecipesManage.AuditAnchor.Verify;

public sealed class AuditAnchorPublicCertificateCatalog : Dictionary<string, RSA>, IDisposable
{
    private const int MaximumPemBytes = 1024 * 1024;
    private const int MaximumCertificates = 256;
    private const string CertificateBeginMarker = "-----BEGIN CERTIFICATE-----";

    private AuditAnchorPublicCertificateCatalog() : base(StringComparer.OrdinalIgnoreCase) { }

    public static AuditAnchorPublicCertificateCatalog Load(string path)
    {
        var files = File.Exists(path)
            ? [Path.GetFullPath(path)]
            : Directory.Exists(path)
                ? Directory.GetFiles(path, "*.pem", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : throw new FileNotFoundException("Certificate file or directory was not found.", path);
        if (files.Length == 0)
            throw new InvalidDataException("Certificate directory contains no PEM files.");
        if (files.Length > MaximumCertificates)
            throw new InvalidDataException($"Certificate directory exceeds the {MaximumCertificates}-file limit.");

        var catalog = new AuditAnchorPublicCertificateCatalog();
        try
        {
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (info.Length is <= 0 or > MaximumPemBytes)
                    throw new InvalidDataException($"Certificate file size is invalid: {info.Name}.");
                var pem = File.ReadAllText(file);
                var firstMarker = pem.IndexOf(CertificateBeginMarker, StringComparison.Ordinal);
                if (firstMarker < 0 ||
                    pem.IndexOf(CertificateBeginMarker, firstMarker + CertificateBeginMarker.Length, StringComparison.Ordinal) >= 0 ||
                    pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal) ||
                    !PemEncoding.TryFind(pem.AsSpan(), out var fields) ||
                    !pem.AsSpan(fields.Label).SequenceEqual("CERTIFICATE"))
                    throw new InvalidDataException($"Expected one public-only PEM CERTIFICATE: {info.Name}.");

                var der = Convert.FromBase64String(pem[fields.Base64Data]);
                using var certificate = X509CertificateLoader.LoadCertificate(der);
                var keyId = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
                var publicKey = certificate.GetRSAPublicKey()
                    ?? throw new InvalidDataException($"Certificate does not contain an RSA public key: {info.Name}.");
                if (publicKey.KeySize < 2048)
                {
                    publicKey.Dispose();
                    throw new InvalidDataException($"RSA public key is smaller than 2048 bits: {info.Name}.");
                }
                if (!catalog.TryAdd(keyId, publicKey))
                {
                    publicKey.Dispose();
                    throw new InvalidDataException($"Duplicate certificate KeyId: {keyId}.");
                }
            }
            return catalog;
        }
        catch
        {
            catalog.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var key in Values) key.Dispose();
    }
}
