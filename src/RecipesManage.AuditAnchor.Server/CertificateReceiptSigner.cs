using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RecipesManage.AuditAnchor.Server;

public interface IAuditAnchorReceiptSigner
{
    string KeyId { get; }
    byte[] Sign(ReadOnlySpan<byte> payload);
}

public sealed class CertificateReceiptSigner : IAuditAnchorReceiptSigner, IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly RSA _privateKey;
    private readonly object _gate = new();

    private CertificateReceiptSigner(X509Certificate2 certificate, RSA privateKey)
    {
        _certificate = certificate;
        _privateKey = privateKey;
        KeyId = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public string KeyId { get; }

    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
            return _privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public RSA OpenPublicKey() => _certificate.GetRSAPublicKey()
        ?? throw new CryptographicException("The anchor certificate does not contain an RSA public key.");

    public static CertificateReceiptSigner Load(string thumbprint, DateTimeOffset nowUtc)
    {
        var normalized = new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != 40 && normalized.Length != 64)
            throw new InvalidOperationException("AuditAnchorReceiver:CertificateThumbprint must be a SHA-1 or SHA-256 certificate thumbprint.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificate = store.Certificates
            .OfType<X509Certificate2>()
            .SingleOrDefault(candidate =>
                string.Equals(candidate.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Convert.ToHexString(candidate.GetCertHash(HashAlgorithmName.SHA256)),
                    normalized, StringComparison.OrdinalIgnoreCase));
        if (certificate is null)
            throw new InvalidOperationException("The configured audit anchor signing certificate was not found in LocalMachine\\My.");

        if (nowUtc < certificate.NotBefore.ToUniversalTime() || nowUtc > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException("The configured audit anchor signing certificate is outside its validity period.");
        }

        var privateKey = certificate.GetRSAPrivateKey();
        if (privateKey is null || privateKey.KeySize < 2048)
        {
            privateKey?.Dispose();
            certificate.Dispose();
            throw new InvalidOperationException("The audit anchor certificate must expose an RSA private key of at least 2048 bits to the receiver service identity.");
        }
        return new CertificateReceiptSigner(certificate, privateKey);
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        _certificate.Dispose();
    }
}
