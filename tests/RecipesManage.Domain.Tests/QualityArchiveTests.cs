using RecipesManage.Domain.Batches;
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
        Assert.NotEqual(8.04, bound["升温斜率"]);
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
        Assert.Equal(95, bound["硬度下限"]);
        Assert.Equal(530, bound["PLC:Temperature"]);
        Assert.False(QualityArchive.Evaluate(step, bound).Any(r => r.OutOfSpec));
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
