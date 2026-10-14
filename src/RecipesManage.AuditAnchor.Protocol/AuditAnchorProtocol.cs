using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RecipesManage.AuditAnchor.Protocol;

public sealed record AuditAnchorStreamRoot(string Stream, long Count, string RootSha256);

public sealed record AuditAnchorManifest(
    int FormatVersion,
    string SourceId,
    string SnapshotId,
    string SnapshotAtUtc,
    string ProductVersion,
    string? SchemaWatermark,
    IReadOnlyList<AuditAnchorStreamRoot> Streams);

public sealed record AuditAnchorReceipt(
    int FormatVersion,
    string SourceId,
    long Sequence,
    string SnapshotId,
    string ManifestSha256,
    string? PreviousReceiptHash,
    string ReceivedAtUtc,
    string KeyId,
    string SignatureAlgorithm,
    string ReceiptHash,
    string SignatureBase64);

public sealed record AuditAnchorReceiptEntry(AuditAnchorManifest Manifest, AuditAnchorReceipt Receipt);

public sealed record AuditAnchorReceiptPage(
    string SourceId,
    long LatestSequence,
    IReadOnlyList<AuditAnchorReceiptEntry> Entries);

public static class AuditAnchorProtocol
{
    public const int CurrentVersion = 1;
    public const string SignatureAlgorithm = "RSA-PSS-SHA256";
    public const string AuditLogsStream = "audit_logs";
    public const string SignatureRecordsStream = "signature_records";

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] CanonicalManifest(AuditAnchorManifest manifest)
    {
        ValidateManifest(manifest);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("sourceId", CanonicalGuid(manifest.SourceId));
            writer.WriteString("snapshotId", CanonicalGuid(manifest.SnapshotId));
            writer.WriteString("snapshotAtUtc", CanonicalUtc(manifest.SnapshotAtUtc));
            writer.WriteString("productVersion", manifest.ProductVersion);
            if (manifest.SchemaWatermark is null) writer.WriteNull("schemaWatermark");
            else writer.WriteString("schemaWatermark", manifest.SchemaWatermark);
            writer.WriteStartArray("streams");
            foreach (var stream in manifest.Streams.OrderBy(x => x.Stream, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("stream", stream.Stream);
                writer.WriteNumber("count", stream.Count);
                writer.WriteString("rootSha256", stream.RootSha256.ToLowerInvariant());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static byte[] CanonicalReceiptPayload(AuditAnchorReceipt receipt)
    {
        ValidateReceiptFields(receipt);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", receipt.FormatVersion);
            writer.WriteString("sourceId", CanonicalGuid(receipt.SourceId));
            writer.WriteNumber("sequence", receipt.Sequence);
            writer.WriteString("snapshotId", CanonicalGuid(receipt.SnapshotId));
            writer.WriteString("manifestSha256", receipt.ManifestSha256.ToLowerInvariant());
            if (receipt.PreviousReceiptHash is null) writer.WriteNull("previousReceiptHash");
            else writer.WriteString("previousReceiptHash", receipt.PreviousReceiptHash.ToLowerInvariant());
            writer.WriteString("receivedAtUtc", CanonicalUtc(receipt.ReceivedAtUtc));
            writer.WriteString("keyId", receipt.KeyId.ToLowerInvariant());
            writer.WriteString("signatureAlgorithm", receipt.SignatureAlgorithm);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static string ManifestHash(AuditAnchorManifest manifest) =>
        Hex(SHA256.HashData(CanonicalManifest(manifest)));

    public static AuditAnchorReceipt SignReceipt(
        string sourceId,
        long sequence,
        string snapshotId,
        string manifestSha256,
        string? previousReceiptHash,
        DateTimeOffset receivedAtUtc,
        string keyId,
        RSA privateKey) =>
        SignReceipt(sourceId, sequence, snapshotId, manifestSha256, previousReceiptHash, receivedAtUtc,
            keyId, payload => privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

    public static AuditAnchorReceipt SignReceipt(
        string sourceId,
        long sequence,
        string snapshotId,
        string manifestSha256,
        string? previousReceiptHash,
        DateTimeOffset receivedAtUtc,
        string keyId,
        Func<byte[], byte[]> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);
        var receipt = new AuditAnchorReceipt(
            CurrentVersion,
            CanonicalGuid(sourceId),
            sequence,
            CanonicalGuid(snapshotId),
            manifestSha256.ToLowerInvariant(),
            previousReceiptHash?.ToLowerInvariant(),
            FormatUtc(receivedAtUtc),
            keyId.ToLowerInvariant(),
            SignatureAlgorithm,
            "",
            "");
        var payload = CanonicalReceiptPayload(receipt);
        var hash = Hex(SHA256.HashData(payload));
        var signature = Convert.ToBase64String(sign(payload));
        return receipt with { ReceiptHash = hash, SignatureBase64 = signature };
    }

    public static bool VerifyReceipt(AuditAnchorReceipt receipt, RSA publicKey, string expectedKeyId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        try
        {
            if (!string.Equals(receipt.KeyId, expectedKeyId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.SignatureAlgorithm, SignatureAlgorithm, StringComparison.Ordinal))
                return false;

            var payload = CanonicalReceiptPayload(receipt);
            var calculatedHash = Hex(SHA256.HashData(payload));
            if (!FixedHexEquals(calculatedHash, receipt.ReceiptHash)) return false;
            var signature = Convert.FromBase64String(receipt.SignatureBase64);
            return publicKey.VerifyData(
                payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    public static void ValidateManifest(AuditAnchorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion != CurrentVersion)
            throw new InvalidDataException("Unsupported audit anchor manifest version.");
        _ = CanonicalGuid(manifest.SourceId);
        _ = CanonicalGuid(manifest.SnapshotId);
        _ = CanonicalUtc(manifest.SnapshotAtUtc);
        if (string.IsNullOrWhiteSpace(manifest.ProductVersion) || manifest.ProductVersion.Length > 128)
            throw new InvalidDataException("Invalid product version.");
        if (manifest.SchemaWatermark?.Length > 128)
            throw new InvalidDataException("Invalid schema watermark.");
        if (manifest.Streams is null || manifest.Streams.Count != 2 || manifest.Streams.Any(x => x is null))
            throw new InvalidDataException("Exactly two audit streams are required.");

        var streams = manifest.Streams.OrderBy(x => x.Stream, StringComparer.Ordinal).ToArray();
        if (streams[0].Stream != AuditLogsStream || streams[1].Stream != SignatureRecordsStream)
            throw new InvalidDataException("Unknown or duplicate audit stream.");
        foreach (var stream in streams)
        {
            if (stream.Count < 0 || !IsSha256(stream.RootSha256))
                throw new InvalidDataException($"Invalid root for stream '{stream.Stream}'.");
        }
    }

    private static void ValidateReceiptFields(AuditAnchorReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.FormatVersion != CurrentVersion || receipt.Sequence <= 0)
            throw new InvalidDataException("Invalid receipt version or sequence.");
        _ = CanonicalGuid(receipt.SourceId);
        _ = CanonicalGuid(receipt.SnapshotId);
        _ = CanonicalUtc(receipt.ReceivedAtUtc);
        if (!IsSha256(receipt.ManifestSha256) ||
            (receipt.PreviousReceiptHash is not null && !IsSha256(receipt.PreviousReceiptHash)))
            throw new InvalidDataException("Invalid receipt hash.");
        if (string.IsNullOrWhiteSpace(receipt.KeyId) || receipt.KeyId.Length > 128 ||
            receipt.SignatureAlgorithm != SignatureAlgorithm)
            throw new InvalidDataException("Invalid receipt signing key.");
    }

    private static string CanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id))
            throw new InvalidDataException("Expected a canonical GUID.");
        return id.ToString("D").ToLowerInvariant();
    }

    private static string CanonicalUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Expected an ISO-8601 UTC timestamp.");
        return FormatUtc(timestamp);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool FixedHexEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}

/// <summary>RFC-6962-style tree shape with domain-separated BRMES leaves and nodes.</summary>
public sealed class AuditMerkleAccumulator : IDisposable
{
    private static readonly byte[] LeafDomain = Encoding.ASCII.GetBytes("BRMES-AUDIT-ANCHOR-LEAF-V1");
    private static readonly byte[] EmptyDomain = Encoding.ASCII.GetBytes("BRMES-AUDIT-ANCHOR-EMPTY-V1");
    private readonly byte[] _stream;
    private readonly byte[]?[] _frontier = new byte[64][];
    private readonly IncrementalHash _leafHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _count;

    public AuditMerkleAccumulator(string stream)
    {
        if (stream is not (AuditAnchorProtocol.AuditLogsStream or AuditAnchorProtocol.SignatureRecordsStream))
            throw new ArgumentOutOfRangeException(nameof(stream));
        _stream = Encoding.ASCII.GetBytes(stream);
    }

    public long Count => _count;

    public void Append(ReadOnlySpan<byte> canonicalRow)
    {
        Span<byte> separator = stackalloc byte[2] { 0, 0 };
        _leafHasher.AppendData(separator[..1]);
        _leafHasher.AppendData(LeafDomain);
        _leafHasher.AppendData(separator[1..]);
        _leafHasher.AppendData(_stream);
        _leafHasher.AppendData(separator[1..]);
        _leafHasher.AppendData(canonicalRow);
        var node = _leafHasher.GetHashAndReset();
        var level = 0;
        while (_frontier[level] is not null)
        {
            node = HashNode(_frontier[level]!, node);
            _frontier[level] = null;
            level++;
        }
        _frontier[level] = node;
        _count++;
    }

    public string FinishHex()
    {
        byte[]? root = null;
        for (var level = 0; level < _frontier.Length; level++)
        {
            var subtree = _frontier[level];
            if (subtree is null) continue;
            root = root is null ? subtree : HashNode(subtree, root);
        }

        if (root is null)
        {
            using var emptyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            emptyHash.AppendData(new byte[] { 2 });
            emptyHash.AppendData(EmptyDomain);
            emptyHash.AppendData(new byte[] { 0 });
            emptyHash.AppendData(_stream);
            root = emptyHash.GetHashAndReset();
        }
        return Convert.ToHexString(root).ToLowerInvariant();
    }

    private static byte[] HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> input = stackalloc byte[65];
        input[0] = 1;
        left.CopyTo(input[1..33]);
        right.CopyTo(input[33..]);
        return SHA256.HashData(input);
    }

    public void Dispose() => _leafHasher.Dispose();
}
