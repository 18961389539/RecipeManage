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
        var allowed = new Dictionary<string, IReadOnlySet<StepType>>(StringComparer.OrdinalIgnoreCase)
        {
            ["QUENCH"] = new HashSet<StepType> { StepType.Cool },
            ["FURNACE"] = new HashSet<StepType> { StepType.Heat, StepType.Hold, StepType.Cool }
        };

        EquipmentClassRules.EnsureCompatible(
            [("S30", StepType.Cool, "UP-淬火"), ("S40", StepType.QualityCheck, "UP-QC")],
            unit => unit == "UP-淬火" ? quench : furnace,
            codes, classes, allowed);

        var ex = Assert.Throws<DomainException>(() => EquipmentClassRules.EnsureCompatible(
            [("S10", StepType.Heat, "UP-淬火")],
            _ => quench, codes, classes, allowed));
        Assert.Equal("EQ_CLASS", ex.Code);
    }

    [Fact]
    public void UnclassifiedEquipment_SkipsCapabilityGate()
    {
        var id = Guid.NewGuid();
        EquipmentClassRules.EnsureCompatible(
            [("S10", StepType.Mix, "UP-混合")],
            _ => id,
            new Dictionary<Guid, string> { [id] = "HT-X" },
            new Dictionary<Guid, string?> { [id] = null },
            new Dictionary<string, IReadOnlySet<StepType>>());
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
    }
}
