using System.Security.Cryptography;
using RecipesManage.AuditAnchor.Protocol;

namespace RecipesManage.AuditAnchor.Verify;

public sealed record AuditAnchorVerificationResult(
    string SourceId,
    long LatestSequence,
    int ReceiptCount,
    string? HeadReceiptHash,
    IReadOnlyCollection<string> SigningKeyIds);

public static class AuditAnchorReceiptVerifier
{
    public static AuditAnchorVerificationResult Verify(
        IEnumerable<AuditAnchorReceiptPage> pages,
        IReadOnlyDictionary<string, RSA> publicKeysByKeyId,
        long? expectedHeadSequence = null,
        string? expectedHeadHash = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(publicKeysByKeyId);
        if (expectedHeadSequence.HasValue != !string.IsNullOrWhiteSpace(expectedHeadHash) ||
            (expectedHeadSequence.HasValue && expectedHeadSequence.Value <= 0))
            throw new ArgumentException("Expected head must include a positive sequence and SHA-256 hash.");

        var pageList = pages.ToArray();
        if (pageList.Length == 0 || pageList.Any(x => x is null))
            throw new InvalidDataException("At least one non-null receipt page is required.");

        var sourceId = CanonicalGuid(pageList[0].SourceId);
        var latestSequence = pageList[0].LatestSequence;
        if (latestSequence < 0 || pageList.Any(x =>
                !string.Equals(CanonicalGuid(x.SourceId), sourceId, StringComparison.Ordinal) ||
                x.LatestSequence != latestSequence))
            throw new InvalidDataException("Receipt pages must describe the same source and ledger head.");

        var receipts = new SortedDictionary<long, AuditAnchorReceiptEntry>();
        foreach (var page in pageList)
        {
            if (page.Entries is null)
                throw new InvalidDataException("Receipt page has no entries collection.");

            foreach (var entry in page.Entries)
            {
                if (entry?.Manifest is null || entry.Receipt is null)
                    throw new InvalidDataException("Receipt page contains an incomplete entry.");

                var manifest = entry.Manifest;
                var receipt = entry.Receipt;
                AuditAnchorProtocol.ValidateManifest(manifest);
                if (!string.Equals(CanonicalGuid(manifest.SourceId), sourceId, StringComparison.Ordinal) ||
                    !string.Equals(CanonicalGuid(receipt.SourceId), sourceId, StringComparison.Ordinal) ||
                    !string.Equals(CanonicalGuid(receipt.SnapshotId), CanonicalGuid(manifest.SnapshotId), StringComparison.Ordinal))
                    throw new InvalidDataException("Manifest and receipt source/snapshot identifiers do not match.");

                var manifestHash = AuditAnchorProtocol.ManifestHash(manifest);
                if (!FixedHexEquals(manifestHash, receipt.ManifestSha256))
                    throw new InvalidDataException($"Manifest digest mismatch at receipt sequence {receipt.Sequence}.");
                if (!publicKeysByKeyId.TryGetValue(receipt.KeyId, out var publicKey) ||
                    !AuditAnchorProtocol.VerifyReceipt(receipt, publicKey, receipt.KeyId))
                    throw new InvalidDataException($"Receipt signature is invalid or its key is unavailable at sequence {receipt.Sequence}.");
                if (!receipts.TryAdd(receipt.Sequence, entry))
                    throw new InvalidDataException($"Duplicate receipt sequence {receipt.Sequence}.");
            }
        }

        if (receipts.Count != latestSequence)
            throw new InvalidDataException($"Incomplete receipt chain: expected {latestSequence} receipts, found {receipts.Count}.");

        string? previousHash = null;
        long sequence = 0;
        var keyIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (receiptSequence, entry) in receipts)
        {
            var receipt = entry.Receipt;
            if (receiptSequence != sequence + 1 ||
                !string.Equals(receipt.PreviousReceiptHash, previousHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Receipt chain is broken at sequence {receiptSequence}.");
            sequence = receiptSequence;
            previousHash = receipt.ReceiptHash;
            keyIds.Add(receipt.KeyId.ToLowerInvariant());
        }

        if (expectedHeadSequence is not null &&
            (expectedHeadSequence != latestSequence || !FixedHexEquals(expectedHeadHash!, previousHash ?? "")))
            throw new InvalidDataException("Verified ledger head does not match the independently pinned checkpoint.");

        return new AuditAnchorVerificationResult(
            sourceId, latestSequence, receipts.Count, previousHash, keyIds.ToArray());
    }

    private static string CanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed))
            throw new InvalidDataException("Expected a canonical source GUID.");
        return parsed.ToString("D").ToLowerInvariant();
    }

    private static bool FixedHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64 ||
            !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
    }
}
