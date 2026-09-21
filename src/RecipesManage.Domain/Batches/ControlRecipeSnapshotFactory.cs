using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

public static class ControlRecipeSnapshotFactory
{
    public static ControlRecipeSnapshot From(
        MasterRecipe recipe,
        RecipeVersion version,
        DateTimeOffset now,
        double scaleFactor = 1,
        string? lotNumber = null,
        IReadOnlyDictionary<string, Guid>? unitEquipment = null,
        Guid primaryEquipmentId = default)
    {
        if (version.Status != RecipeStatus.Approved)
            throw new DomainException("NOT_APPROVED", "只能从已批准的主配方版本生成控制配方快照。");
        if (scaleFactor <= 0 || double.IsNaN(scaleFactor) || double.IsInfinity(scaleFactor))
            throw new DomainException("SCALE", "批次缩放因子必须为正数。");

        var ordered = RecipeTopology.Order(version.Steps, version.Edges);
        var steps = ordered.Select((step, index) => new SnapshotStep
        {
            StepId = step.Id,
            Code = step.Code,
            Name = step.Name,
            Type = step.Type,
            Ordinal = index,
            WatchdogSeconds = step.WatchdogSeconds,
            UnitProcedure = step.UnitProcedure,
            Operation = step.Operation,
            Parameters = step.Parameters
                .OrderBy(p => p.SlotIndex)
                .Select(p => new SnapshotParameter
                {
                    SlotIndex = p.SlotIndex,
                    Name = p.Name,
                    EngineeringUnit = p.EngineeringUnit,
                    Setpoint = Scale(p.Setpoint, p.ScaleWithBatch, scaleFactor),
                    Min = p.Min is double min ? Scale(min, p.ScaleWithBatch, scaleFactor) : null,
                    Max = p.Max is double max ? Scale(max, p.ScaleWithBatch, scaleFactor) : null,
                    WriteToPlc = p.WriteToPlc,
                    ArchiveAsQuality = p.ArchiveAsQuality,
                    ScaleWithBatch = p.ScaleWithBatch
                })
                .ToList()
        }).ToList();

        return new ControlRecipeSnapshot
        {
            MasterRecipeId = recipe.Id,
            RecipeVersionId = version.Id,
            VersionNumber = version.VersionNumber,
            RecipeCode = recipe.Code,
            RecipeName = recipe.Name,
            ProductCode = recipe.ProductCode,
            ProductName = recipe.ProductName,
            FrozenAt = now,
            ScaleFactor = Math.Abs(scaleFactor - 1) < 1e-12 ? null : scaleFactor,
            LotNumber = string.IsNullOrWhiteSpace(lotNumber) ? null : lotNumber.Trim(),
            UnitEquipment = UnitEquipmentBinding.Normalize(
                unitEquipment,
                ordered.Select(s => Isa88.UnitName(s.UnitProcedure)).ToList(),
                primaryEquipmentId),
            Steps = steps,
            Edges = version.Edges.Select(e => new SnapshotEdge { FromStepId = e.FromStepId, ToStepId = e.ToStepId }).ToList()
        };
    }

    public static double Scale(double value, bool scaleWithBatch, double factor) =>
        scaleWithBatch ? Math.Round(value * factor, 6) : value;
}
