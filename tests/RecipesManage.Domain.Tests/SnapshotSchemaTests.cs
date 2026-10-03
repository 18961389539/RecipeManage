using System.Text.Json;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

/// <summary>
/// 控制配方快照的结构版本：新批次写 schemaVersion；旧批次（没有该字段）必须仍能打开并通过校验；
/// 比本程序新的版本不被解释。下面三条黄金样本是固定的真实 JSON——它们一旦不再 Valid，就意味着旧批记录打不开了。
/// </summary>
public sealed class SnapshotSchemaTests
{
    private static readonly JsonSerializerOptions Options = SnapshotJson.Options;

    // 当前版本（1）封好的样本。
    private const string GoldenV1 =
        """{"schemaVersion":1,"masterRecipeId":"11111111-1111-1111-1111-111111111111","recipeVersionId":"22222222-2222-2222-2222-222222222222","versionNumber":3,"recipeCode":"AL-HT-T6","recipeName":"Al-6061-T6 heat treatment","productCode":"AL6061","productName":"Aluminium alloy","frozenAt":"2026-09-07T00:00:00+08:00","integrityHash":"249CB94DCE82EE8F7CAC416CAE3ED5FAB57F449DBAE213DB6C1AA6A0F8374072","steps":[{"stepId":"33333333-3333-3333-3333-333333333333","code":"S20","name":"Soak","type":"Hold","ordinal":0,"watchdogSeconds":120,"parameters":[{"slotIndex":0,"name":"Soak temperature","engineeringUnit":"degC","setpoint":533,"min":525,"max":535,"writeToPlc":true,"archiveAsQuality":true}]}],"edges":[]}""";

    // 加版本号之前封的批次：没有 schemaVersion 字段，哈希是 V2 口径。
    private const string GoldenPreVersionV2 =
        """{"masterRecipeId":"11111111-1111-1111-1111-111111111111","recipeVersionId":"22222222-2222-2222-2222-222222222222","versionNumber":3,"recipeCode":"AL-HT-T6","recipeName":"Al-6061-T6 heat treatment","productCode":"AL6061","productName":"Aluminium alloy","frozenAt":"2026-09-07T00:00:00+08:00","integrityHash":"E9B57A98BC0154D197CEAAF8F0F87FA05EE4FC202177A6FF17FF318DD2954C11","steps":[{"stepId":"33333333-3333-3333-3333-333333333333","code":"S20","name":"Soak","type":"Hold","ordinal":0,"watchdogSeconds":120,"parameters":[{"slotIndex":0,"name":"Soak temperature","engineeringUnit":"degC","setpoint":533,"min":525,"max":535,"writeToPlc":true,"archiveAsQuality":true}]}],"edges":[]}""";

    // 更早的批次：哈希只覆盖步骤与连线（V1 口径）。
    private const string GoldenPreVersionV1 =
        """{"masterRecipeId":"11111111-1111-1111-1111-111111111111","recipeVersionId":"22222222-2222-2222-2222-222222222222","versionNumber":3,"recipeCode":"AL-HT-T6","recipeName":"Al-6061-T6 heat treatment","productCode":"AL6061","productName":"Aluminium alloy","frozenAt":"2026-09-07T00:00:00+08:00","integrityHash":"6329C05FDDA3B5C3DCC9B656E5B1ADDDC3D974891D92EFD9C6B60CEB439EC9D7","steps":[{"stepId":"33333333-3333-3333-3333-333333333333","code":"S20","name":"Soak","type":"Hold","ordinal":0,"watchdogSeconds":120,"parameters":[{"slotIndex":0,"name":"Soak temperature","engineeringUnit":"degC","setpoint":533,"min":525,"max":535,"writeToPlc":true,"archiveAsQuality":true}]}],"edges":[]}""";

    private static string Verify(string json) =>
        SnapshotIntegrity.Verify(SnapshotJson.Deserialize(json), Options);

    [Fact]
    public void Factory_StampsTheCurrentVersion_AndItIsInTheJson()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("SV-1", "schema", "P", "part", null, engineer);
        var draft = recipe.RequireDraft();
        var step = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "temp", "degC", 100, 90, 110, true, true)]);
        draft.ReplaceProcedure([step], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "s", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "q", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);

        var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
        Assert.Equal(SnapshotSchema.Current, snapshot.SchemaVersion);
        SnapshotIntegrity.Seal(snapshot, Options, out var json);

        Assert.Contains($"\"schemaVersion\":{SnapshotSchema.Current}", json, StringComparison.Ordinal);
        Assert.Equal(SnapshotIntegrity.Valid, Verify(json));
    }

    [Fact]
    public void GoldenCurrentVersion_StaysValid() =>
        Assert.Equal(SnapshotIntegrity.Valid, Verify(GoldenV1));

    [Fact]
    public void GoldenPreVersionBatches_StillOpen_AsVersionZero()
    {
        var v2 = SnapshotJson.Deserialize(GoldenPreVersionV2)!;
        Assert.Equal(SnapshotSchema.Legacy, v2.SchemaVersion);
        Assert.Equal(SnapshotIntegrity.Valid, SnapshotIntegrity.Verify(v2, Options));
        Assert.Equal(SnapshotIntegrity.Valid, Verify(GoldenPreVersionV1));
    }

    [Fact]
    public void Sealing_AVersionZeroSnapshot_KeepsTheOldHashGeneration()
    {
        // 旧口径仍可封存（测试与回放用），且与历史批次的哈希一致。
        var legacy = SnapshotJson.Deserialize(GoldenPreVersionV2)!;
        legacy.IntegrityHash = null;
        SnapshotIntegrity.Seal(legacy, Options, out _);
        Assert.Equal("E9B57A98BC0154D197CEAAF8F0F87FA05EE4FC202177A6FF17FF318DD2954C11", legacy.IntegrityHash);
    }

    [Fact]
    public void ChangingTheVersionNumber_BreaksTheSeal_ItCannotBeDowngradedToTheOldHash()
    {
        // 版本 1 的快照把 schemaVersion 改回 0（想让它走不含版本号的旧口径）：对不上。
        var downgraded = GoldenV1.Replace("\"schemaVersion\":1", "\"schemaVersion\":0", StringComparison.Ordinal);
        Assert.Equal(SnapshotIntegrity.Mismatch, Verify(downgraded));
        // 删掉字段等价于 0。
        var stripped = GoldenV1.Replace("\"schemaVersion\":1,", "", StringComparison.Ordinal);
        Assert.Equal(SnapshotIntegrity.Mismatch, Verify(stripped));
    }

    [Fact]
    public void EditingContent_OfAVersionedSnapshot_IsStillCaught() =>
        Assert.Equal(SnapshotIntegrity.Mismatch,
            Verify(GoldenV1.Replace("\"setpoint\":533", "\"setpoint\":999", StringComparison.Ordinal)));

    [Fact]
    public void UpgradingAPreVersionSnapshot_ByAddingTheField_IsCaught()
    {
        // 旧批次被人手工加上 schemaVersion:1 想"升级"：V3 对不上。
        var upgraded = GoldenPreVersionV2.Replace("{\"masterRecipeId\"", "{\"schemaVersion\":1,\"masterRecipeId\"",
            StringComparison.Ordinal);
        Assert.Equal(SnapshotIntegrity.Mismatch, Verify(upgraded));
    }

    [Fact]
    public void ANewerVersion_IsUnsupported_NotMismatch_AndNeverPassesTheGate()
    {
        var future = GoldenV1.Replace("\"schemaVersion\":1", $"\"schemaVersion\":{SnapshotSchema.Current + 1}",
            StringComparison.Ordinal);
        var status = Verify(future);
        Assert.Equal(SnapshotIntegrity.Unsupported, status);

        var ex = Assert.Throws<DomainException>(() => SnapshotIntegrity.DemandSealed(status));
        Assert.Equal("SNAPSHOT_SCHEMA", ex.Code);
        Assert.Throws<DomainException>(() => SnapshotSchema.DemandSupported(SnapshotJson.Deserialize(future)!));
    }

    [Fact]
    public void ANegativeVersion_IsCorrupt()
    {
        var bad = GoldenV1.Replace("\"schemaVersion\":1", "\"schemaVersion\":-1", StringComparison.Ordinal);
        Assert.Equal(SnapshotIntegrity.Corrupt, Verify(bad));
    }

    [Fact]
    public void Unsupported_IsNotLegacy_EvenWithoutAHash()
    {
        // 没有哈希的新版本快照不能被当成"历史快照"放行。
        var future = GoldenV1.Replace("\"schemaVersion\":1", $"\"schemaVersion\":{SnapshotSchema.Current + 1}",
            StringComparison.Ordinal);
        var snapshot = SnapshotJson.Deserialize(future)!;
        snapshot.IntegrityHash = null;
        Assert.Equal(SnapshotIntegrity.Unsupported, SnapshotIntegrity.Verify(snapshot, Options));
    }
}
