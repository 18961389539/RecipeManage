using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class QualityArchiveTests
{
    [Fact]
    public void Bind_UsesRecipeParameterNamesForArchivedSlots()
    {
        var step = new SnapshotStep
        {
            Code = "S20",
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "保温温度", EngineeringUnit = "℃", Setpoint = 530, ArchiveAsQuality = true },
                new SnapshotParameter { SlotIndex = 1, Name = "保温时长", EngineeringUnit = "s", Setpoint = 8, ArchiveAsQuality = true },
                new SnapshotParameter { SlotIndex = 2, Name = "斜率", EngineeringUnit = "℃/min", Setpoint = 8, ArchiveAsQuality = false }
            ]
        };

        var bound = QualityArchive.Bind(step, new Dictionary<string, double>
        {
            ["Temperature"] = 532.4,
            ["HoldTime"] = 8.04,
            ["Pressure"] = 1.06
        });

        Assert.Equal(532.4, bound["保温温度"]);
        Assert.Equal(8.04, bound["保温时长"]);
        Assert.False(bound.ContainsKey("斜率"));
        Assert.Equal(532.4, bound["PLC:Temperature"]);
    }

    [Fact]
    public void Bind_RampUnitWithMojibakeDoesNotStealHoldTime()
    {
        var step = new SnapshotStep
        {
            Code = "S10",
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "升温斜率", EngineeringUnit = "?/min", Setpoint = 8, ArchiveAsQuality = true },
                new SnapshotParameter { SlotIndex = 1, Name = "保温时长", EngineeringUnit = "s", Setpoint = 8, ArchiveAsQuality = true }
            ]
        };
        var bound = QualityArchive.Bind(step, new Dictionary<string, double>
        {
            ["Temperature"] = 530,
            ["HoldTime"] = 8.04,
            ["Pressure"] = 1.06
        });
        Assert.Equal(8.04, bound["保温时长"]);
        Assert.False(bound.ContainsKey("升温斜率"));
    }

    [Fact]
    public void Bind_HardnessDoesNotStealTemperature()
    {
        var step = new SnapshotStep
        {
            Code = "S50",
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "硬度下限", EngineeringUnit = "HB", Setpoint = 95, Min = 90, Max = 110, ArchiveAsQuality = true }
            ]
        };
        var bound = QualityArchive.Bind(step, new Dictionary<string, double> { ["Temperature"] = 530, ["HoldTime"] = 1 });
        Assert.False(bound.ContainsKey("硬度下限"));
        Assert.Equal(530, bound["PLC:Temperature"]);
        var row = Assert.Single(QualityArchive.Evaluate(step, bound));
        Assert.True(row.Unmeasured);
        Assert.False(row.OutOfSpec);
    }

    [Fact]
    public void Evaluate_FlagsTemperatureOutOfSpec()
    {
        var step = new SnapshotStep
        {
            Code = "S10",
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530, Min = 520, Max = 540, ArchiveAsQuality = true }
            ]
        };
        var bound = QualityArchive.Bind(step, new Dictionary<string, double> { ["Temperature"] = 500 });
        var row = Assert.Single(QualityArchive.Evaluate(step, bound));
        Assert.True(row.OutOfSpec);
        Assert.Equal("目标温度", row.Name);
    }

    [Theory]
    [InlineData("保温时长", "s", ParameterSemantic.Unspecified, null, "HoldTime")]      // 历史配方：靠名称推断
    [InlineData("目标温度", "℃", ParameterSemantic.Unspecified, null, "Temperature")]
    [InlineData("缸压", "bar", ParameterSemantic.Unspecified, null, "Pressure")]
    [InlineData("粘度", "cP", ParameterSemantic.Unspecified, null, null)]              // 推不出来 = 永远归档不到
    [InlineData("粘度", "cP", ParameterSemantic.MeasuredValue, null, null)]            // 声明了却没给点，同样不行
    [InlineData("粘度", "cP", ParameterSemantic.MeasuredValue, "Viscosity", "Viscosity")]
    [InlineData("随便什么名", "", ParameterSemantic.Unspecified, "Torque", "Torque")]  // 声明永远优先
    [InlineData("硬度下限", "HB", ParameterSemantic.Unspecified, null, null)]          // 实验室量不许从 PLC 通道冒充
    public void ResolveSourceTag_IsTheSingleAnswerForWhereAnArchivedValueComesFrom(
        string name, string unit, ParameterSemantic semantic, string? declared, string? expected) =>
        Assert.Equal(expected, QualityArchive.ResolveSourceTag(name, unit, semantic, declared));

    [Fact]
    public void DemandArchivableSources_RejectsTheCaseThatUsedToSurfaceOnlyAtRelease()
    {
        var versionId = Guid.NewGuid();
        RecipeStep Coating(bool thicknessDeclared) => new(
            versionId, "S10", "涂布", StepType.Pressure, 0, 0, 0, 30, null,
            [
                new RecipeParameter(0, "目标压力", "bar", 4, 1, 10, true, true),                       // 可推断，放过
                new RecipeParameter(1, "涂层厚度", "μm", 60, 55, 65, false, true, false,
                    ParameterSemantic.MeasuredValue, thicknessDeclared ? "Gauge" : null),              // 没点就没来源
                new RecipeParameter(2, "涂布速度", "m/min", 3, 1, 8, true, false)
            ]);

        var e = Assert.Throws<DomainException>(
            () => QualityArchive.DemandArchivableSources([Coating(false)]));
        Assert.Equal("QUALITY_SOURCE", e.Code);
        Assert.Contains("涂层厚度", e.Message, StringComparison.Ordinal);
        Assert.Contains("S10", e.Message, StringComparison.Ordinal);
        // 只点出问题不够，得说清下一步做什么，否则工程师会去猜是哪个开关错了。
        Assert.Contains("实测点", e.Message, StringComparison.Ordinal);

        // 补上实测点声明就通过：这正是"非热处工艺"能自证的路径。
        QualityArchive.DemandArchivableSources([Coating(true)]);
    }

    [Fact]
    public void FormatSetpointVsActual_IncludesArchivedMeasurementAndOos()
    {
        var step = new SnapshotStep
        {
            Code = "S10",
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530, Min = 520, Max = 540, ArchiveAsQuality = true }
            ]
        };
        var ok = Assert.Single(QualityArchive.EvaluateJson(step, """{"目标温度":529.4}"""));
        Assert.Equal("530 / 529.4", QualityArchive.FormatSetpointVsActual(530, ok));
        var oos = new QualitySpecResult("目标温度", 500, 520, 540, true);
        Assert.Contains("超差", QualityArchive.FormatSetpointVsActual(530, oos), StringComparison.Ordinal);
        Assert.Equal("530", QualityArchive.FormatSetpointVsActual(530, null));
    }
}
