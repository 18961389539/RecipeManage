using System.Security.Cryptography;
using System.Text.Json;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

/// <summary>
/// Stable command content for execution signatures. Runtime state and telemetry are intentionally
/// excluded because they continue changing after the command is signed.
/// </summary>
public static class BatchActionSignatureContent
{
    public const int CurrentVersion = 1;

    public static bool Supports(string action) => action is
        "batch.start.esign" or
        "batch.retry.esign" or
        "batch.abort.esign" or
        "batch.hold.esign" or
        "batch.resume.esign" or
        "batch.skip.esign" or
        "batch.confirm.esign";

    public static string Compute(
        string action,
        string entityType,
        string entityId,
        string meaning,
        string? signerName,
        string? detail,
        ControlRecipeSnapshot snapshot)
    {
        var content = new
        {
            Version = CurrentVersion,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Meaning = meaning,
            SignerName = signerName,
            Detail = NormalizeDetail(detail),
            Snapshot = BatchRecordEvidenceHash.SnapshotContent(snapshot)
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(content, SnapshotJson.Options);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    public static string Integrity(SignatureRecord signature, ControlRecipeSnapshot snapshot)
    {
        if (signature.ContentHashVersion is null && signature.ContentHash is null)
            return "Unbound";
        if (signature.ContentHashVersion is null || string.IsNullOrWhiteSpace(signature.ContentHash))
            return "Mismatch";
        if (signature.ContentHashVersion != CurrentVersion)
            return "Unsupported";

        var currentHash = Compute(
            signature.Action,
            signature.EntityType,
            signature.EntityId,
            signature.Meaning,
            signature.SignerName,
            signature.Detail,
            snapshot);
        return BatchRecordEvidenceHash.Matches(
            signature.ContentHashVersion, signature.ContentHash, currentHash)
            ? "Verified"
            : "Mismatch";
    }

    internal static string? NormalizeDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
}
