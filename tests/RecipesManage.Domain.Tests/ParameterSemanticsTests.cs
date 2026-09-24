using System.Text.Json;
using System.Text.Json.Serialization;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

/// <summary>
/// 参数语义识别的不变量集合：未显式声明语义时，按名称/单位推断的行为。
/// 历史配方与已密封快照都依赖它，引入显式语义后必须逐条保持。
/// </summary>
public sealed class ParameterSemanticsTests
{
    private static SnapshotStep Step(params SnapshotParameter[] parameters) =>
        new() { Code = "S10", Parameters = parameters };

    private static readonly Dictionary<string, double> Measured = new()
    {
        ["Temperature"] = 532.4,
        ["Pressure"] = 1.06,
        ["HoldTime"] = 8.04
    };

    [Fact]
    public void Bind_MatchesDurationTemperaturePressureByName()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "保温温度", EngineeringUnit = "℃", ArchiveAsQuality = true },
            new SnapshotParameter { SlotIndex = 1, Name = "保温时长", EngineeringUnit = "s", ArchiveAsQuality = true },
            new SnapshotParameter { SlotIndex = 2, Name = "目标压力", EngineeringUnit = "bar", ArchiveAsQuality = true });

        var bound = QualityArchive.Bind(step, Measured);

        Assert.Equal(532.4, bound["保温温度"]);
        Assert.Equal(8.04, bound["保温时长"]);
        Assert.Equal(1.06, bound["目标压力"]);
    }

    [Fact]
    public void Bind_MatchesDurationByUnitAlone()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "Hold", EngineeringUnit = "min", ArchiveAsQuality = true },
            new SnapshotParameter { SlotIndex = 1, Name = "Stage", EngineeringUnit = "sec", ArchiveAsQuality = true });

        var bound = QualityArchive.Bind(step, Measured);

        Assert.Equal(8.04, bound["Hold"]);
        Assert.False(bound.ContainsKey("Stage"));
    }

    [Fact]
    public void Bind_DoesNotStealTagsForHardnessOrRate()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "硬度下限", EngineeringUnit = "HB", ArchiveAsQuality = true },
            new SnapshotParameter { SlotIndex = 1, Name = "升温斜率", EngineeringUnit = "?/min", ArchiveAsQuality = true },
            new SnapshotParameter { SlotIndex = 2, Name = "冲洗流量", EngineeringUnit = "L/min", ArchiveAsQuality = true });

        var bound = QualityArchive.Bind(step, Measured);

        Assert.False(bound.ContainsKey("硬度下限"));
        Assert.False(bound.ContainsKey("升温斜率"));
        Assert.False(bound.ContainsKey("冲洗流量"));
    }

    [Fact]
    public void Bind_EachMeasuredTagIsConsumedAtMostOnce()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "保温时长", EngineeringUnit = "s", ArchiveAsQuality = true },
            new SnapshotParameter { SlotIndex = 1, Name = "等待时间", EngineeringUnit = "", ArchiveAsQuality = true });

        var bound = QualityArchive.Bind(step, Measured);

        Assert.Equal(8.04, bound["保温时长"]);
        Assert.False(bound.ContainsKey("等待时间"));
    }

    [Fact]
    public void Bind_AlwaysExposesRawPlcTags()
    {
        var bound = QualityArchive.Bind(Step(), Measured);

        Assert.Equal(532.4, bound["PLC:Temperature"]);
        Assert.Equal(1.06, bound["PLC:Pressure"]);
        Assert.Equal(8.04, bound["PLC:HoldTime"]);
    }

    [Theory]
    [InlineData("保温时长", "s", 12, 12)]
    [InlineData("等待时长", "min", 2, 120)]
    [InlineData("保温", "h", 2, 7200)]
    [InlineData("保温", "hr", 2, 7200)]
    [InlineData("等待", "", 90, 90)]
    public void TryFrom_ReadsDurationInParameterOrder(string name, string unit, double setpoint, double expectedSeconds)
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530 },
            new SnapshotParameter { SlotIndex = 1, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8 },
            new SnapshotParameter { SlotIndex = 2, Name = name, EngineeringUnit = unit, Setpoint = setpoint });

        Assert.Equal(expectedSeconds, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }

    [Fact]
    public void TryFrom_ReturnsNullWhenOnlyRatePresent()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530 },
            new SnapshotParameter { SlotIndex = 1, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8 });

        Assert.Null(ProcessDuration.TryFrom(step));
    }

    [Fact]
    public void TryFrom_PrefersLowestSlotIndex()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 1, Name = "保温时长", EngineeringUnit = "s", Setpoint = 30 },
            new SnapshotParameter { SlotIndex = 0, Name = "等待时长", EngineeringUnit = "s", Setpoint = 10 });

        Assert.Equal(10, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }

    [Theory]
    [InlineData("保温时长", "s", ParameterSemantic.Duration)]
    [InlineData("保温时间", "", ParameterSemantic.Duration)]
    [InlineData("Hold time", "min", ParameterSemantic.Duration)]
    [InlineData("阶段时长", "sec", ParameterSemantic.Duration)]
    [InlineData("保温", "hr", ParameterSemantic.Duration)]
    [InlineData("升温斜率", "℃/min", ParameterSemantic.Rate)]
    [InlineData("斜率", "?/min", ParameterSemantic.Rate)]
    [InlineData("Ramp rate", "bar/s", ParameterSemantic.Rate)]
    [InlineData("冲洗流量", "L/min", ParameterSemantic.Rate)]
    [InlineData("硬度下限", "HB", ParameterSemantic.Unspecified)]
    [InlineData("装炉量", "kg", ParameterSemantic.Unspecified)]
    [InlineData("搅拌转速", "rpm", ParameterSemantic.Unspecified)]
    public void Infer_FallsBackToNameAndUnit(string name, string unit, ParameterSemantic expected) =>
        Assert.Equal(expected, ParameterSemantics.Infer(name, unit));

    [Fact]
    public void Resolve_PrefersDeclarationOverInference()
    {
        Assert.Equal(ParameterSemantic.Rate, ParameterSemantics.Resolve(ParameterSemantic.Rate, "保温时长", "s"));
        Assert.Equal(ParameterSemantic.Duration, ParameterSemantics.Resolve(ParameterSemantic.Duration, "粘度", "cP"));
        Assert.Equal(ParameterSemantic.Duration, ParameterSemantics.Resolve(ParameterSemantic.Unspecified, "保温时长", "s"));
    }

    [Fact]
    public void Bind_UsesDeclaredMeasuredTagForParametersWithoutKeywords()
    {
        var step = Step(
            new SnapshotParameter
            {
                SlotIndex = 0, Name = "粘度", EngineeringUnit = "cP", ArchiveAsQuality = true, MeasuredTag = "Viscosity"
            });

        var bound = QualityArchive.Bind(step, new Dictionary<string, double>
        {
            ["Temperature"] = 532.4,
            ["Viscosity"] = 118.2
        });

        Assert.Equal(118.2, bound["粘度"]);
    }

    [Fact]
    public void Bind_DeclaredTagIsNotStolenByAnEarlierInferredParameter()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "保温时长", EngineeringUnit = "s", ArchiveAsQuality = true },
            new SnapshotParameter
            {
                SlotIndex = 1, Name = "实际保温", EngineeringUnit = "s", ArchiveAsQuality = true, MeasuredTag = "HoldTime"
            });

        var bound = QualityArchive.Bind(step, Measured);

        Assert.Equal(8.04, bound["实际保温"]);
        Assert.False(bound.ContainsKey("保温时长"));
    }

    [Fact]
    public void Bind_MissingDeclaredTagLeavesParameterUnmeasured()
    {
        var step = Step(
            new SnapshotParameter
            {
                SlotIndex = 0, Name = "粘度", EngineeringUnit = "cP", Setpoint = 120, Min = 100, Max = 140,
                ArchiveAsQuality = true, MeasuredTag = "Viscosity"
            });

        var bound = QualityArchive.Bind(step, Measured);
        Assert.False(bound.ContainsKey("粘度"));
        var row = Assert.Single(QualityArchive.Evaluate(step, bound));
        Assert.True(row.Unmeasured);
        Assert.False(row.OutOfSpec);
    }

    [Fact]
    public void TryFrom_PrefersDeclaredDurationOverAnEarlierInferredOne()
    {
        var step = Step(
            new SnapshotParameter { SlotIndex = 0, Name = "等待时长", EngineeringUnit = "s", Setpoint = 10 },
            new SnapshotParameter
            {
                SlotIndex = 1, Name = "发酵周期", EngineeringUnit = "min", Setpoint = 2, Semantic = ParameterSemantic.Duration
            });

        Assert.Equal(120, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }

    [Fact]
    public void TryFrom_IgnoresParametersDeclaredAsRate()
    {
        var step = Step(
            new SnapshotParameter
            {
                SlotIndex = 0, Name = "保温时长", EngineeringUnit = "s", Setpoint = 12, Semantic = ParameterSemantic.Rate
            });

        Assert.Null(ProcessDuration.TryFrom(step));
    }

    [Fact]
    public void TryFrom_ReadsDeclaredDurationWithoutTimeKeywords()
    {
        var step = Step(
            new SnapshotParameter
            {
                SlotIndex = 0, Name = "Cure cycle", EngineeringUnit = "h", Setpoint = 2, Semantic = ParameterSemantic.Duration
            });

        Assert.Equal(7200, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }

    [Fact]
    public void SnapshotParameter_OmitsNewFieldsWhenUnset_SoSealedHashesStayValid()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        var legacy = JsonSerializer.Serialize(
            new SnapshotParameter { SlotIndex = 0, Name = "保温温度", EngineeringUnit = "℃", Setpoint = 530 }, options);
        Assert.DoesNotContain("semantic", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("measuredTag", legacy, StringComparison.Ordinal);

        var declared = JsonSerializer.Serialize(
            new SnapshotParameter
            {
                SlotIndex = 0, Name = "粘度", EngineeringUnit = "cP", Setpoint = 120,
                Semantic = ParameterSemantic.Duration, MeasuredTag = "Viscosity"
            }, options);
        Assert.Contains("\"semantic\":\"Duration\"", declared, StringComparison.Ordinal);
        Assert.Contains("\"measuredTag\":\"Viscosity\"", declared, StringComparison.Ordinal);
        Assert.Equal(ParameterSemantic.Duration,
            JsonSerializer.Deserialize<SnapshotParameter>(declared, options)!.Semantic);
    }
}
