using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
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

    [Theory]
    [InlineData("固化时长", "d", 1, 86400)]
    [InlineData("固化时长", "天", 2, 172800)]
    [InlineData("day", "days", 1, 86400)]
    [InlineData("振动时长", "ms", 1500, 1.5)]
    [InlineData("振动时长", "毫秒", 2000, 2)]
    [InlineData("点胶时长", "分钟", 3, 180)]
    [InlineData("点胶时长", "小时", 1, 3600)]
    public void TryFrom_ConvertsEveryUnitItClaimsToUnderstand(string name, string unit, double setpoint, double expectedSeconds)
    {
        // 换算表与"这算不算时长"的关键词表必须同步：以前写了 "2 d" 既不换算也不被当成时长，
        // 于是看门狗与剩余秒数双双失真，配方却看起来完全正常。
        var step = new SnapshotStep
        {
            Parameters = [new SnapshotParameter { SlotIndex = 0, Name = name, EngineeringUnit = unit, Setpoint = setpoint }]
        };
        Assert.Equal(expectedSeconds, ProcessDuration.TryFrom(step)!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(0)]                      // 没有时长语义：放行，不占时长槽
    [InlineData(0.2)]                    // 下限本身
    [InlineData(7200)]                   // 上限本身
    [InlineData(86400)]                  // 24 小时固化：这一版 PLC 程序表达不了
    [InlineData(0.05)]                   // 50 毫秒：低于时长槽分辨率
    public void DemandWritable_FailsLoudInsteadOfMovingTheSetpoint(double seconds)
    {
        var expected = seconds > 7200 || (seconds > 0 && seconds < ProcessDuration.MinWritableSeconds);
        var e = Record.Exception(() => ProcessDuration.DemandWritable("工步 S40（时效保温）", TimeSpan.FromSeconds(seconds)));
        if (expected)
        {
            var d = Assert.IsType<DomainException>(e);
            Assert.Equal("DURATION_RANGE", d.Code);
            // 报错里要能看见真实时长与被改的方向，否则工程师不知道该拆工步还是改单位。
            Assert.Contains("工步 S40", d.Message, StringComparison.Ordinal);
            Assert.Contains(seconds > 7200 ? "超过" : "短于", d.Message, StringComparison.Ordinal);
        }
        else Assert.Null(e);
    }

    [Fact]
    public void PackParameters_RejectsOverlongDuration_InsteadOfWritingAShorterOne()
    {
        var step = new SnapshotStep
        {
            Code = "S40",
            Name = "时效保温",
            Type = StepType.Hold,
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "保温温度", EngineeringUnit = "℃", Setpoint = 175, ArchiveAsQuality = true },
                new SnapshotParameter { SlotIndex = 1, Name = "保温时长", EngineeringUnit = "h", Setpoint = 24, ArchiveAsQuality = true }
            ]
        };

        // 过去这里 Math.Clamp 成 7200 写给 PLC，而上位机仍按 24 小时等：设定值被改了，履历上看不出来。
        var e = Assert.Throws<DomainException>(() => ControlRecipeWritePlan.PackParameters(step));
        Assert.Equal("DURATION_RANGE", e.Code);
    }

    [Fact]
    public void Format_UsesTheUnitAnOperatorWouldSay()
    {
        Assert.Equal("1 d", ProcessDuration.Format(TimeSpan.FromDays(1)));
        Assert.Equal("1.5 h", ProcessDuration.Format(TimeSpan.FromMinutes(90)));
        Assert.Equal("0.2 s", ProcessDuration.Format(TimeSpan.FromSeconds(0.2)));
        Assert.Equal("0.5 ms", ProcessDuration.Format(TimeSpan.FromMilliseconds(0.5)));
    }
}
