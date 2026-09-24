using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class EquipmentClassLibraryTests
{
    [Fact]
    public void AddTemplate_RejectsHostPhaseAndDuplicateProgram()
    {
        var cls = new EquipmentClass("PROCESS", "工艺", null);
        cls.AddTemplate("PH-XFER", "转移", StepType.Transfer, null, 60, [], 6);

        var host = Assert.Throws<DomainException>(() =>
            cls.AddTemplate("PH-WAIT", "等待", StepType.Wait, null, 30, [], null));
        Assert.Equal("PHASE_HOST", host.Code);

        var dup = Assert.Throws<DomainException>(() =>
            cls.AddTemplate("PH-XFER2", "再转移", StepType.Transfer, null, 60, [], 6));
        Assert.Equal("PLC_PROGRAM", dup.Code);
    }

    [Fact]
    public void UpdateTemplate_CanRetargetProgram_RemoveDropsCapability()
    {
        var cls = new EquipmentClass("PROCESS", "工艺", null);
        var rinse = cls.AddTemplate("PH-RINSE", "水冲洗", StepType.Transfer, "OP-Rinse 水冲洗", 30,
        [
            new PhaseParameterSpec { SlotIndex = 1, Name = "冲洗时长", EngineeringUnit = "s", Setpoint = 3, WriteToPlc = true }
        ], 21);
        Assert.Contains(21, cls.AllowedProgramIds());

        cls.UpdateTemplate(rinse.Id, "喷淋冲洗", StepType.Transfer, "OP-Spray", 45,
        [
            new PhaseParameterSpec { SlotIndex = 1, Name = "冲洗时长", EngineeringUnit = "s", Setpoint = 5, WriteToPlc = true }
        ], 23);
        Assert.Equal("喷淋冲洗", rinse.Name);
        Assert.Equal(23, rinse.PlcProgramId);
        Assert.Contains(23, cls.AllowedProgramIds());
        Assert.DoesNotContain(21, cls.AllowedProgramIds());

        cls.RemoveTemplate(rinse);
        Assert.DoesNotContain(23, cls.AllowedProgramIds());
    }
}
