using System.Text.Encodings.Web;
using System.Text.Json;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class SnapshotIntegrityTests
{
    [Fact]
    public void SealAndVerify_RoundTrips()
    {
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 3,
            RecipeCode = "AL-HT-T6",
            RecipeName = "Al-6061-T6 热处理",
            ProductCode = "AL6061",
            ProductName = "铝合金",
            FrozenAt = DateTimeOffset.Parse("2026-09-07T00:00:00+08:00"),
            Steps =
            [
                new SnapshotStep
                {
                    StepId = Guid.NewGuid(),
                    Code = "S20",
                    Name = "固溶保温",
                    Type = StepType.Hold,
                    Ordinal = 0,
                    WatchdogSeconds = 120,
                    Parameters =
                    [
                        new SnapshotParameter
                        {
                            SlotIndex = 0, Name = "保温温度", EngineeringUnit = "℃", Setpoint = 533,
                            Min = 525, Max = 535, WriteToPlc = true, ArchiveAsQuality = true
                        }
                    ]
                }
            ]
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        SnapshotIntegrity.Seal(snapshot, options, out var json);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.IntegrityHash));
        var loaded = JsonSerializer.Deserialize<ControlRecipeSnapshot>(json, options);
        Assert.Equal(SnapshotIntegrity.Valid, SnapshotIntegrity.Verify(loaded, options));
        Assert.DoesNotContain("scaleWithBatch", json, StringComparison.Ordinal);
        Assert.DoesNotContain("scaleFactor", json, StringComparison.Ordinal);

        loaded = JsonSerializer.Deserialize<ControlRecipeSnapshot>(
            json.Replace("\"setpoint\":533", "\"setpoint\":999", StringComparison.Ordinal), options);
        Assert.Equal(SnapshotIntegrity.Mismatch, SnapshotIntegrity.Verify(loaded, options));

        var legacy = JsonSerializer.Deserialize<ControlRecipeSnapshot>(json, options)!;
        legacy.IntegrityHash = null;
        Assert.Equal(SnapshotIntegrity.Legacy, SnapshotIntegrity.Verify(legacy, options));
    }

    [Fact]
    public void Verify_IgnoresNullIsa88FieldsSoLegacySnapshotsStayValid()
    {
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "AL-HT-T6",
            RecipeName = "heat",
            ProductCode = "P",
            ProductName = "prod",
            FrozenAt = DateTimeOffset.Parse("2026-09-07T00:00:00+08:00"),
            Steps =
            [
                new SnapshotStep
                {
                    StepId = Guid.NewGuid(),
                    Code = "S10",
                    Name = "heat",
                    Type = StepType.Heat,
                    Ordinal = 0,
                    WatchdogSeconds = 60,
                    Parameters = []
                }
            ]
        };
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        SnapshotIntegrity.Seal(snapshot, options, out var json);
        Assert.DoesNotContain("unitProcedure", json, StringComparison.Ordinal);
        Assert.DoesNotContain("equipmentClassCode", json, StringComparison.Ordinal);
        var loaded = JsonSerializer.Deserialize<ControlRecipeSnapshot>(json, options);
        Assert.Equal(SnapshotIntegrity.Valid, SnapshotIntegrity.Verify(loaded, options));

        var withIsa = new ControlRecipeSnapshot
        {
            MasterRecipeId = snapshot.MasterRecipeId,
            RecipeVersionId = snapshot.RecipeVersionId,
            VersionNumber = snapshot.VersionNumber,
            RecipeCode = snapshot.RecipeCode,
            RecipeName = snapshot.RecipeName,
            ProductCode = snapshot.ProductCode,
            ProductName = snapshot.ProductName,
            FrozenAt = snapshot.FrozenAt,
            Steps =
            [
                new SnapshotStep
                {
                    StepId = snapshot.Steps[0].StepId,
                    Code = "S10",
                    Name = "heat",
                    Type = StepType.Heat,
                    Ordinal = 0,
                    WatchdogSeconds = 60,
                    UnitProcedure = Isa88.DefaultUnitProcedure,
                    Operation = Isa88.DefaultOperation(StepType.Heat),
                    Parameters = []
                }
            ]
        };
        var hashWithout = snapshot.IntegrityHash;
        SnapshotIntegrity.Seal(withIsa, options, out _);
        Assert.NotEqual(hashWithout, withIsa.IntegrityHash);
    }

    [Fact]
    public void Verify_UnitEquipmentChangeInvalidatesNewSeal()
    {
        var snapshot = BaseSnapshot("AL-HT-2UP", "parallel");
        var options = JsonOpts();
        SnapshotIntegrity.Seal(snapshot, options, out var json);
        Assert.DoesNotContain("unitEquipment", json, StringComparison.Ordinal);

        var withMap = Copy(snapshot, unitEquipment: new Dictionary<string, Guid> { ["UP-淬火"] = Guid.NewGuid() });
        Assert.Equal(SnapshotIntegrity.Mismatch, SnapshotIntegrity.Verify(withMap, options));
    }

    [Fact]
    public void Verify_LotNumberChangeInvalidatesNewSeal()
    {
        var snapshot = BaseSnapshot("AL-HT-T6", "heat");
        var options = JsonOpts();
        SnapshotIntegrity.Seal(snapshot, options, out var json);
        Assert.DoesNotContain("lotNumber", json, StringComparison.Ordinal);
        var withLot = Copy(snapshot, lotNumber: "INGOT-01");
        Assert.Equal(SnapshotIntegrity.Mismatch, SnapshotIntegrity.Verify(withLot, options));
    }

    [Fact]
    public void Verify_AcceptsHistoricalV1HashWhenBindingFieldsWereAddedLater()
    {
        var snapshot = BaseSnapshot("AL-HT-T6", "heat");
        var options = JsonOpts();
        snapshot.IntegrityHash = SnapshotIntegrity.ComputeHashV1(snapshot, options);
        var withLot = Copy(snapshot, lotNumber: "INGOT-01");
        Assert.Equal(SnapshotIntegrity.Valid, SnapshotIntegrity.Verify(withLot, options));
    }

    [Fact]
    public void DemandSealed_RejectsMismatch()
    {
        var ex = Assert.Throws<DomainException>(() => SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Mismatch));
        Assert.Equal("SNAPSHOT_INTEGRITY", ex.Code);
        SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Valid);
        SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Legacy);
    }

    private static ControlRecipeSnapshot BaseSnapshot(string code, string name) => new()
    {
        MasterRecipeId = Guid.NewGuid(),
        RecipeVersionId = Guid.NewGuid(),
        VersionNumber = 1,
        RecipeCode = code,
        RecipeName = name,
        ProductCode = "P",
        ProductName = "prod",
        FrozenAt = DateTimeOffset.Parse("2026-09-07T00:00:00+08:00"),
        Steps = []
    };

    private static ControlRecipeSnapshot Copy(
        ControlRecipeSnapshot snapshot,
        string? lotNumber = null,
        IReadOnlyDictionary<string, Guid>? unitEquipment = null) => new()
    {
        MasterRecipeId = snapshot.MasterRecipeId,
        RecipeVersionId = snapshot.RecipeVersionId,
        VersionNumber = snapshot.VersionNumber,
        RecipeCode = snapshot.RecipeCode,
        RecipeName = snapshot.RecipeName,
        ProductCode = snapshot.ProductCode,
        ProductName = snapshot.ProductName,
        FrozenAt = snapshot.FrozenAt,
        IntegrityHash = snapshot.IntegrityHash,
        LotNumber = lotNumber ?? snapshot.LotNumber,
        UnitEquipment = unitEquipment ?? snapshot.UnitEquipment,
        Steps = snapshot.Steps,
        Edges = snapshot.Edges
    };

    private static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
