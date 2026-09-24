using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class EquipmentClassRulesTests
{
    [Fact]
    public void QuenchClass_RejectsHeat_AllowsCoolAndQualityCheck()
    {
        var quench = Guid.NewGuid();
        var furnace = Guid.NewGuid();
        var classes = new Dictionary<Guid, string?> { [quench] = "QUENCH", [furnace] = "FURNACE" };
        var codes = new Dictionary<Guid, string> { [quench] = "HT-02", [furnace] = "HT-01" };
        var allowed = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["QUENCH"] = new HashSet<int> { (int)StepType.Cool },
            ["FURNACE"] = new HashSet<int> { (int)StepType.Heat, (int)StepType.Hold, (int)StepType.Cool }
        };

        EquipmentClassRules.EnsureCompatible(
            [("S30", StepType.Cool, null, "UP-淬火", null), ("S40", StepType.QualityCheck, null, "UP-QC", null)],
            unit => unit == "UP-淬火" ? quench : furnace,
            codes, classes, allowed);

        var ex = Assert.Throws<DomainException>(() => EquipmentClassRules.EnsureCompatible(
            [("S10", StepType.Heat, null, "UP-淬火", null)],
            _ => quench, codes, classes, allowed));
        Assert.Equal("EQ_CLASS", ex.Code);
    }

    [Fact]
    public void UnclassifiedEquipment_SkipsCapabilityGate()
    {
        var id = Guid.NewGuid();
        EquipmentClassRules.EnsureCompatible(
            [("S10", StepType.Mix, null, "UP-混合", null)],
            _ => id,
            new Dictionary<Guid, string> { [id] = "HT-X" },
            new Dictionary<Guid, string?> { [id] = null },
            new Dictionary<string, IReadOnlySet<int>>());
    }

    [Fact]
    public void PhaseTemplate_MaterializesRecipeParameters()
    {
        var cls = new EquipmentClass("FURNACE", "炉", null);
        var template = cls.AddTemplate("PH-HEAT", "升温至设定点", StepType.Heat, null, 180,
        [
            new PhaseParameterSpec
            {
                SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530,
                Min = 520, Max = 540, WriteToPlc = true, ArchiveAsQuality = true
            }
        ]);
        var parameters = template.MaterializeParameters();
        Assert.Equal("PH-HEAT", template.Code);
        Assert.Equal(StepType.Heat, template.StepType);
        Assert.Contains(parameters, p => p.Name == "目标温度" && p.WriteToPlc && p.Setpoint == 530);
        Assert.Contains(StepType.Heat, cls.AllowedProcessTypes());
        Assert.DoesNotContain(StepType.Mix, cls.AllowedProcessTypes());
        Assert.Contains((int)StepType.Heat, cls.AllowedProgramIds());
        Assert.DoesNotContain(21, cls.AllowedProgramIds());
    }

    [Fact]
    public void CustomRinseProgram_RejectedOnFurnace_AllowedOnProcess()
    {
        var furnaceEq = Guid.NewGuid();
        var processEq = Guid.NewGuid();
        var furnace = new EquipmentClass("FURNACE", "炉", null);
        furnace.AddTemplate("PH-HEAT", "升温", StepType.Heat, null, 180, [], (int)StepType.Heat);
        var process = new EquipmentClass("PROCESS", "工艺", null);
        process.AddTemplate("PH-RINSE", "水冲洗", StepType.Transfer, "OP-Rinse 水冲洗", 30, [], 21);

        var allowed = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FURNACE"] = furnace.AllowedProgramIds(),
            ["PROCESS"] = process.AllowedProgramIds()
        };
        var codes = new Dictionary<Guid, string> { [furnaceEq] = "HT-01", [processEq] = "PR-01" };
        var classes = new Dictionary<Guid, string?> { [furnaceEq] = "FURNACE", [processEq] = "PROCESS" };

        EquipmentClassRules.EnsureCompatible(
            [("S10", StepType.Transfer, 21, "UP-冲洗", null)],
            _ => processEq, codes, classes, allowed);

        var ex = Assert.Throws<DomainException>(() => EquipmentClassRules.EnsureCompatible(
            [("S10", StepType.Transfer, 21, "UP-固溶", null)],
            _ => furnaceEq, codes, classes, allowed));
        Assert.Equal("EQ_CLASS", ex.Code);
        Assert.Contains("21", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredProcess_RejectsFurnace_EvenForHostQualityCheck()
    {
        var furnaceEq = Guid.NewGuid();
        var processEq = Guid.NewGuid();
        var codes = new Dictionary<Guid, string> { [furnaceEq] = "HT-01", [processEq] = "PR-01" };
        var classes = new Dictionary<Guid, string?> { [furnaceEq] = "FURNACE", [processEq] = "PROCESS" };
        var allowed = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase);

        EquipmentClassRules.EnsureCompatible(
            [("S30", StepType.QualityCheck, null, "UP-QC", "PROCESS")],
            _ => processEq, codes, classes, allowed);

        var ex = Assert.Throws<DomainException>(() => EquipmentClassRules.EnsureCompatible(
            [("S30", StepType.QualityCheck, null, "UP-QC", "PROCESS")],
            _ => furnaceEq, codes, classes, allowed));
        Assert.Equal("EQ_CLASS", ex.Code);
        Assert.Contains("PROCESS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericEquipment_DoesNotConflictWithDeclaredProcess()
    {
        var id = Guid.NewGuid();
        EquipmentClassRules.EnsureCompatible(
            [("S30", StepType.QualityCheck, null, "UP-QC", "PROCESS")],
            _ => id,
            new Dictionary<Guid, string> { [id] = "MB-01" },
            new Dictionary<Guid, string?> { [id] = "GENERIC" },
            new Dictionary<string, IReadOnlySet<int>>());
    }
}
