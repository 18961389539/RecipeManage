using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class ControlRecipeWritePlanTests
{
    [Fact]
    public void Heat_PacksSetpointsAndDuration_UsesStableStepId()
    {
        var step = Heat("S10", 0, 530, 8);
        var item = ControlRecipeWritePlan.ForStep(step);
        Assert.True(item.WriteToPlc);
        Assert.Equal(10, item.PlcStepId);
        Assert.Equal((int)StepType.Heat, item.PlcStepType);
        Assert.Equal(530, item.Parameters[0], 3);
        Assert.Equal(8, item.Parameters[1], 3);
        Assert.Equal(8, item.Parameters[15], 3);
        Assert.Contains("Trigger_Write", item.Policy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StepType.Wait)]
    [InlineData(StepType.ManualConfirm)]
    [InlineData(StepType.QualityCheck)]
    public void HostSideSteps_DoNotWritePlc(StepType type)
    {
        var step = new SnapshotStep
        {
            StepId = Guid.NewGuid(),
            Code = "S20",
            Name = "host",
            Type = type,
            Ordinal = 1,
            WatchdogSeconds = 30,
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 999, WriteToPlc = true }
            ]
        };
        var item = ControlRecipeWritePlan.ForStep(step);
        Assert.False(item.WriteToPlc);
        Assert.All(item.Parameters, v => Assert.Equal(0, v));
        Assert.Contains("禁止写 PLC", item.Policy, StringComparison.Ordinal);
        Assert.False(ControlRecipeWritePlan.WritesToPlc(type));
    }

    [Fact]
    public void FromSnapshot_FollowsTopologyOrder_WorkContextMatchesPack()
    {
        var heat = Heat("S10", 0, 120, 2);
        var wait = new SnapshotStep
        {
            StepId = Guid.NewGuid(),
            Code = "S20",
            Name = "wait",
            Type = StepType.Wait,
            Ordinal = 1,
            WatchdogSeconds = 30,
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "等待时长", EngineeringUnit = "s", Setpoint = 8, WriteToPlc = false }
            ]
        };
        var snapshot = new ControlRecipeSnapshot { Steps = [heat, wait] };
        var plan = ControlRecipeWritePlan.FromSnapshot(snapshot);
        Assert.Equal(["S10", "S20"], plan.Select(p => p.StepCode).ToArray());
        Assert.True(plan[0].WriteToPlc);
        Assert.False(plan[1].WriteToPlc);

        var work = ControlRecipeWritePlan.ToWorkContext(heat);
        Assert.Equal(10, work.StepId);
        Assert.Equal(plan[0].Parameters, work.Parameters);
    }

    [Fact]
    public void CustomPlcProgramId_IsWhatHandshakeReceives()
    {
        var step = new SnapshotStep
        {
            StepId = Guid.NewGuid(),
            Code = "S40",
            Name = "水冲洗",
            Type = StepType.Transfer,
            PlcProgramId = 21,
            Ordinal = 3,
            WatchdogSeconds = 30,
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "冲洗流量", EngineeringUnit = "L/min", Setpoint = 12, WriteToPlc = true },
                new SnapshotParameter { SlotIndex = 1, Name = "冲洗时长", EngineeringUnit = "s", Setpoint = 3, WriteToPlc = true }
            ]
        };
        var item = ControlRecipeWritePlan.ForStep(step);
        Assert.True(item.WriteToPlc);
        Assert.Equal(21, item.PlcStepType);
        Assert.Equal(21, ControlRecipeWritePlan.ToWorkContext(step).StepType);
        Assert.Equal(3, item.Parameters[15], 3);
    }

    [Fact]
    public void ScaledSnapshotSetpoint_IsWhatEngineWouldWrite()
    {
        var step = Heat("S30", 2, ControlRecipeSnapshotFactory.Scale(50, true, 2), 1);
        step = new SnapshotStep
        {
            StepId = step.StepId,
            Code = step.Code,
            Name = step.Name,
            Type = step.Type,
            Ordinal = step.Ordinal,
            WatchdogSeconds = step.WatchdogSeconds,
            Parameters =
            [
                new SnapshotParameter
                {
                    SlotIndex = 0, Name = "转移量", EngineeringUnit = "kg", Setpoint = 100,
                    WriteToPlc = true, ScaleWithBatch = true
                },
                new SnapshotParameter { SlotIndex = 1, Name = "时长", EngineeringUnit = "s", Setpoint = 1, WriteToPlc = true }
            ]
        };
        Assert.Equal(100, ControlRecipeWritePlan.PackParameters(step)[0], 3);
    }

    [Fact]
    public void WrittenSlots_CoversEveryPackedSlotIncludingDuration()
    {
        // 驱动只写点表里有的前 N 槽，这份清单就是"点表槽数够不够"的判据；
        // 漏掉时长槽等于放过"设备带着上一步的时长跑"这种静默失效。
        Assert.Equal([0, 1, ControlRecipeWritePlan.DurationSlot], ControlRecipeWritePlan.WrittenSlots(Heat("S10", 0, 530, 8)));
    }

    [Fact]
    public void WrittenSlots_IsEmptyForHostSideSteps_AndSkipsSlotsNothingIsPackedInto()
    {
        var wait = Step(StepType.Wait,
            new SnapshotParameter { SlotIndex = 0, Name = "等待时长", EngineeringUnit = "s", Setpoint = 8, WriteToPlc = true });
        Assert.Empty(ControlRecipeWritePlan.WrittenSlots(wait));

        // 只有斜率、没有时长：槽 15 不会被写，所以点表短于 16 槽也承载得住这一步。
        var ramp = Step(StepType.Heat,
            new SnapshotParameter { SlotIndex = 4, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8, WriteToPlc = true });
        Assert.Equal([4], ControlRecipeWritePlan.WrittenSlots(ramp));

        // 斜率不写 PLC 但时长要写：只有槽 15 进帧。
        var held = Step(StepType.Heat,
            new SnapshotParameter { SlotIndex = 4, Name = "升温斜率", EngineeringUnit = "℃/min", Setpoint = 8, WriteToPlc = false },
            new SnapshotParameter { SlotIndex = 5, Name = "保温时长", EngineeringUnit = "s", Setpoint = 30, WriteToPlc = true });
        Assert.Equal([5, ControlRecipeWritePlan.DurationSlot], ControlRecipeWritePlan.WrittenSlots(held));
    }

    private static SnapshotStep Step(StepType type, params SnapshotParameter[] parameters) =>
        new()
        {
            StepId = Guid.NewGuid(),
            Code = "S90",
            Name = type.ToString(),
            Type = type,
            Ordinal = 0,
            WatchdogSeconds = 30,
            Parameters = parameters
        };

    private static SnapshotStep Heat(string code, int ordinal, double temperature, double seconds) =>
        new()
        {
            StepId = Guid.NewGuid(),
            Code = code,
            Name = "heat",
            Type = StepType.Heat,
            Ordinal = ordinal,
            WatchdogSeconds = 30,
            Parameters =
            [
                new SnapshotParameter { SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = temperature, WriteToPlc = true },
                new SnapshotParameter { SlotIndex = 1, Name = "时长", EngineeringUnit = "s", Setpoint = seconds, WriteToPlc = true }
            ]
        };
}
