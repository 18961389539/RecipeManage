using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class RecipeUnitClassTests
{
    private static IReadOnlyList<(string Code, IReadOnlySet<int> Allowed)> Catalog() =>
    [
        ("FURNACE", new HashSet<int> { 1, 2, 3 }),
        ("QUENCH", new HashSet<int> { 3 }),
        ("PROCESS", new HashSet<int> { 4, 5, 6, 21, 22 }),
        ("GENERIC", new HashSet<int> { 1, 2, 3, 4, 5, 6, 21, 22 })
    ];

    [Fact]
    public void Infer_PicksTightestClass_NotGeneric()
    {
        Assert.Equal("PROCESS", RecipeUnitClass.Infer(
            [(StepType.Transfer, 21), (StepType.Pressure, 22), (StepType.QualityCheck, null)],
            Catalog()));
        Assert.Equal("FURNACE", RecipeUnitClass.Infer(
            [(StepType.Heat, null), (StepType.Hold, null)],
            Catalog()));
        Assert.Equal("QUENCH", RecipeUnitClass.Infer(
            [(StepType.Cool, null)],
            Catalog()));
    }

    [Fact]
    public void Infer_HostOnlyOrUnmatched_ReturnsNull()
    {
        Assert.Null(RecipeUnitClass.Infer([(StepType.QualityCheck, null)], Catalog()));
        Assert.Null(RecipeUnitClass.Infer([(StepType.Transfer, 99)], Catalog()));
    }

    [Fact]
    public void Inherit_SingleJoinSource_TakesThatClass()
    {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UP-冲洗"] = "PROCESS"
        };
        Assert.Equal("PROCESS", RecipeUnitClass.Inherit(
            "UP-QC",
            [("UP-冲洗", "UP-QC")],
            classes));
    }

    [Fact]
    public void Inherit_ParallelJoin_ReturnsNull()
    {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UP-固溶"] = "FURNACE",
            ["UP-淬火"] = "QUENCH"
        };
        Assert.Null(RecipeUnitClass.Inherit(
            "UP-QC",
            [("UP-固溶", "UP-QC"), ("UP-淬火", "UP-QC")],
            classes));
    }

    [Fact]
    public void ResolveDeclared_HostEmpty_InheritsUniqueJoin()
    {
        var declared = RecipeUnitClass.ResolveDeclared(
            [
                ("UP-冲洗", StepType.Transfer, "PROCESS"),
                ("UP-QC", StepType.QualityCheck, null)
            ],
            [("UP-冲洗", "UP-QC")]);
        Assert.Equal("PROCESS", declared["UP-冲洗"]);
        Assert.Equal("PROCESS", declared["UP-QC"]);
    }

    [Fact]
    public void ResolveDeclared_KeepsExplicitHostClass_AndSkipsParallelJoin()
    {
        var kept = RecipeUnitClass.ResolveDeclared(
            [
                ("UP-冲洗", StepType.Transfer, "PROCESS"),
                ("UP-QC", StepType.QualityCheck, "GENERIC")
            ],
            [("UP-冲洗", "UP-QC")]);
        Assert.Equal("GENERIC", kept["UP-QC"]);

        var parallel = RecipeUnitClass.ResolveDeclared(
            [
                ("UP-固溶", StepType.Heat, "FURNACE"),
                ("UP-淬火", StepType.Cool, "QUENCH"),
                ("UP-QC", StepType.QualityCheck, null)
            ],
            [("UP-固溶", "UP-QC"), ("UP-淬火", "UP-QC")]);
        Assert.False(parallel.ContainsKey("UP-QC"));
    }

    [Theory]
    [InlineData("PROCESS", "FURNACE", true)]
    [InlineData("PROCESS", "GENERIC", false)]
    [InlineData("PROCESS", null, false)]
    [InlineData(null, "FURNACE", false)]
    [InlineData("PROCESS", "PROCESS", false)]
    public void Conflicts_OnlyWhenBothSpecificAndDifferent(string? declared, string? actual, bool expect)
    {
        Assert.Equal(expect, RecipeUnitClass.Conflicts(declared, actual));
    }
}
