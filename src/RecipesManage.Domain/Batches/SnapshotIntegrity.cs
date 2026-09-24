using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

public static class SnapshotIntegrity
{
    public const string Valid = "Valid";
    public const string Legacy = "Legacy";
    public const string Mismatch = "Mismatch";
    public const string Corrupt = "Corrupt";

    public static string ComputeHash(ControlRecipeSnapshot snapshot, JsonSerializerOptions options) =>
        ComputeHashV2(snapshot, options);

    public static string Seal(ControlRecipeSnapshot snapshot, JsonSerializerOptions options, out string json)
    {
        var hash = ComputeHashV2(snapshot, options);
        snapshot.IntegrityHash = hash;
        json = JsonSerializer.Serialize(snapshot, options);
        return hash;
    }

    public static string Verify(ControlRecipeSnapshot? snapshot, JsonSerializerOptions options)
    {
        if (snapshot is null)
            return Corrupt;
        if (string.IsNullOrWhiteSpace(snapshot.IntegrityHash))
            return Legacy;
        if (Matches(snapshot.IntegrityHash, ComputeHashV2(snapshot, options)))
            return Valid;
        // 历史密封只覆盖工步/连线。新密封失败时再对 v1，避免把旧库全部打成 Mismatch。
        if (Matches(snapshot.IntegrityHash, ComputeHashV1(snapshot, options)))
            return Valid;
        return Mismatch;
    }

    public static void DemandSealed(string status)
    {
        if (status is Mismatch or Corrupt)
            throw new DomainException("SNAPSHOT_INTEGRITY", "控制配方快照完整性失败，禁止启动或放行。");
    }

    private static bool Matches(string stored, string actual) =>
        string.Equals(stored, actual, StringComparison.OrdinalIgnoreCase);

    internal static string ComputeHashV1(ControlRecipeSnapshot snapshot, JsonSerializerOptions options)
    {
        var canonical = new
        {
            snapshot.MasterRecipeId,
            snapshot.RecipeVersionId,
            snapshot.VersionNumber,
            snapshot.RecipeCode,
            snapshot.RecipeName,
            snapshot.ProductCode,
            snapshot.ProductName,
            snapshot.FrozenAt,
            snapshot.Steps,
            snapshot.Edges
        };
        return Sha256(JsonSerializer.Serialize(canonical, options));
    }

    private static string ComputeHashV2(ControlRecipeSnapshot snapshot, JsonSerializerOptions options)
    {
        var units = snapshot.UnitEquipment?
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { kv.Key, kv.Value })
            .ToList();
        var canonical = new
        {
            snapshot.MasterRecipeId,
            snapshot.RecipeVersionId,
            snapshot.VersionNumber,
            snapshot.RecipeCode,
            snapshot.RecipeName,
            snapshot.ProductCode,
            snapshot.ProductName,
            snapshot.FrozenAt,
            snapshot.ScaleFactor,
            snapshot.LotNumber,
            UnitEquipment = units,
            snapshot.Steps,
            snapshot.Edges
        };
        return Sha256(JsonSerializer.Serialize(canonical, options));
    }

    private static string Sha256(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}
