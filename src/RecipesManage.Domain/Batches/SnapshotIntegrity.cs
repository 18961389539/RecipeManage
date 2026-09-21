using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecipesManage.Domain.Batches;

public static class SnapshotIntegrity
{
    public const string Valid = "Valid";
    public const string Legacy = "Legacy";
    public const string Mismatch = "Mismatch";
    public const string Corrupt = "Corrupt";

    public static string ComputeHash(ControlRecipeSnapshot snapshot, JsonSerializerOptions options)
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
        var json = JsonSerializer.Serialize(canonical, options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

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
        if (string.IsNullOrWhiteSpace(snapshot.IntegrityHash))
            return Legacy;
        var actual = ComputeHash(snapshot, options);
        return string.Equals(actual, snapshot.IntegrityHash, StringComparison.OrdinalIgnoreCase) ? Valid : Mismatch;
    }
}
