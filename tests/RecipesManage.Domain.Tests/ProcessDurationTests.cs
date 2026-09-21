using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class ProcessDurationTests
{
    [Fact]
    public void TryFrom_UsesHoldTimeNotRamp()
    {
        var step = new SnapshotStep
        {
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530 },
                new SnapshotParameter { SlotIndex = 1, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8 },
                new SnapshotParameter { SlotIndex = 2, Name = "保温时长", EngineeringUnit = "s", Setpoint = 12 }
            ]
        };
        Assert.Equal(12, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }

    [Fact]
    public void TryFrom_ConvertsMinutes()
    {
        var step = new SnapshotStep
        {
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "等待时长", EngineeringUnit = "min", Setpoint = 2 }
            ]
        };
        Assert.Equal(120, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }

    [Fact]
    public void TryFrom_IgnoresRampOnlyHeat()
    {
        var step = new SnapshotStep
        {
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530 },
                new SnapshotParameter { SlotIndex = 1, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8 }
            ]
        };
        Assert.Null(ProcessDuration.TryFrom(step));
    }

    [Fact]
    public void TryFrom_UsesHeatDurationNotRamp()
    {
        var step = new SnapshotStep
        {
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530 },
                new SnapshotParameter { SlotIndex = 1, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8 },
                new SnapshotParameter { SlotIndex = 2, Name = "升温时长", EngineeringUnit = "s", Setpoint = 8 }
            ]
        };
        Assert.Equal(8, ProcessDuration.TryFrom(step)!.Value.TotalSeconds);
    }
}
