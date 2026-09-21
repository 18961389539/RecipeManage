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
    [Fact]
    public void Meaning_CoversAuthorSupervisorQuality()
    {
        Assert.Contains("工艺工程师", ElectronicSignature.Meaning(ApprovalLevel.Author, ApprovalDecision.Approved));
        Assert.Contains("工艺主管", ElectronicSignature.Meaning(ApprovalLevel.Supervisor, ApprovalDecision.Approved));
        Assert.Contains("质量", ElectronicSignature.Meaning(ApprovalLevel.Quality, ApprovalDecision.Approved));
    }
}
