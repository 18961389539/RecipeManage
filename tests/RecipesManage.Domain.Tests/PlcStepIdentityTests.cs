using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class PlcStepIdentityTests
{
    [Theory]
    [InlineData("S10", 0, 10)]
    [InlineData("S20", 1, 20)]
    [InlineData("STEP-30", 2, 30)]
    [InlineData("heat", 3, 4)]
    [InlineData("", 0, 1)]
    public void FromCode_UsesStableDigits(string code, int ordinal, int expected) =>
        Assert.Equal(expected, PlcStepIdentity.FromCode(code, ordinal));
}

public sealed class Isa88GroupingTests
{
    [Fact]
    public void GroupByUnitProcedure_SplitsAdjacentSegments()
    {
        var groups = Isa88.GroupByUnitProcedure(
            new[] { "UP-A", "UP-A", "UP-B", "UP-A" },
            x => x);
        Assert.Equal(3, groups.Count);
        Assert.Equal("UP-A", groups[0].UnitProcedure);
        Assert.Equal(2, groups[0].Phases.Count);
        Assert.Equal("UP-B", groups[1].UnitProcedure);
        Assert.Equal("UP-A", groups[2].UnitProcedure);
        Assert.Single(groups[2].Phases);
    }
}

public sealed class ElectronicSignatureTests
{
    // 配方审核节点的签名含义不再由这里按枚举查表——它随审批链配置冻结进 approval_records，
    // 覆盖在 ApprovalChainTests（含"改链不改在审版本"那条）。

    [Theory]
    [InlineData("batch.start.esign", "禁止盲写")]
    [InlineData("batch.hold.esign", "Host_Hold")]
    [InlineData("batch.skip.esign", "主管")]
    [InlineData("batch.release.esign", "质量")]
    public void BatchMeaning_IsFixedStatement(string action, string needle)
    {
        Assert.Contains(needle, ElectronicSignature.Batch(action));
        Assert.StartsWith(ElectronicSignature.Batch(action), ElectronicSignature.AuditDetail(action, "B1"));
        Assert.Equal(ElectronicSignature.ProcedureSave,
            "我作为工艺工程师确认本次 Procedure / Steps / Parameters 变更准确，并记录变更原因。");
    }
}
