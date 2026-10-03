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
    /// <summary>快照由比本程序更新的版本生成（<see cref="SnapshotSchema"/>）：不解释、不放行。</summary>
    public const string Unsupported = "Unsupported";

    /// <summary>按快照自带的结构版本选哈希口径：版本 0 用历史的 V2，版本 ≥1 用把版本号也纳入的 V3。</summary>
    public static string ComputeHash(ControlRecipeSnapshot snapshot, JsonSerializerOptions options) =>
        snapshot.SchemaVersion >= 1 ? ComputeHashV3(snapshot, options) : ComputeHashV2(snapshot, options);

    public static string Seal(ControlRecipeSnapshot snapshot, JsonSerializerOptions options, out string json)
    {
        var hash = ComputeHash(snapshot, options);
        snapshot.IntegrityHash = hash;
        json = JsonSerializer.Serialize(snapshot, options);
        return hash;
    }

    public static string Verify(ControlRecipeSnapshot? snapshot, JsonSerializerOptions options)
    {
        if (snapshot is null)
            return Corrupt;
        // 先看版本再看哈希：新版本的快照哈希口径本程序可能根本不认识，对不上不代表被篡改。
        if (snapshot.SchemaVersion > SnapshotSchema.Current)
            return Unsupported;
        if (!SnapshotSchema.IsSupported(snapshot.SchemaVersion))
            return Corrupt;
        if (string.IsNullOrWhiteSpace(snapshot.IntegrityHash))
            return Legacy;
        // 版本 ≥1 只认 V3，不退回旧口径：否则把 schemaVersion 改成 0 就能让新快照按旧口径（不含版本号）被接受。
        if (snapshot.SchemaVersion >= 1)
            return Matches(snapshot.IntegrityHash, ComputeHashV3(snapshot, options)) ? Valid : Mismatch;
        if (Matches(snapshot.IntegrityHash, ComputeHashV2(snapshot, options)))
            return Valid;
        // 历史密封只覆盖工步/连线。新密封失败时再对 v1，避免把旧库全部打成 Mismatch。
        if (Matches(snapshot.IntegrityHash, ComputeHashV1(snapshot, options)))
            return Valid;
        return Mismatch;
    }

    public static void DemandSealed(string status)
    {
        if (status == Unsupported)
            throw new DomainException("SNAPSHOT_SCHEMA",
                "控制配方快照由更新的程序版本生成，本程序不解释，禁止启动或放行。请升级程序，不要在旧版本上处理这个批次。");
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

    /// <summary>V2 的全部内容，再加结构版本本身：版本号被改动就对不上。</summary>
    private static string ComputeHashV3(ControlRecipeSnapshot snapshot, JsonSerializerOptions options)
    {
        var units = snapshot.UnitEquipment?
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { kv.Key, kv.Value })
            .ToList();
        var canonical = new
        {
            snapshot.SchemaVersion,
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
