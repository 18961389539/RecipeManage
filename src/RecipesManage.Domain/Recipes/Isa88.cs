namespace RecipesManage.Domain.Recipes;

/// <summary>
/// ISA-88 配方层次在本系统中的落点：Master Recipe → Procedure（整张 DAG）
/// → Unit Procedure → Operation → Phase（RecipeStep）。
/// </summary>
public static class Isa88
{
    /// <summary>单元规程缺省名。刻意不含工艺行业词——真实名称由设计器填写。</summary>
    public const string DefaultUnitProcedure = "UP-01";

    public static string DefaultOperation(StepType type) => type switch
    {
        StepType.Heat => "OP-Heat 升温",
        StepType.Hold => "OP-Hold 保温",
        StepType.Cool => "OP-Cool 冷却",
        StepType.Mix => "OP-Mix 搅拌",
        StepType.Pressure => "OP-Press 加压",
        StepType.Transfer => "OP-Xfer 转移",
        StepType.QualityCheck => "OP-QC 质检",
        StepType.ManualConfirm => "OP-Manual 人工确认",
        _ => "OP-Wait 等待"
    };

    public static string UnitName(string? unitProcedure) =>
        string.IsNullOrWhiteSpace(unitProcedure) ? DefaultUnitProcedure : unitProcedure.Trim();

    /// <summary>按拓扑顺序把 Phase 归入连续的 Unit Procedure 段（相邻同名合并，不相邻则新开一段）。</summary>
    public static IReadOnlyList<Isa88UnitGroup<T>> GroupByUnitProcedure<T>(
        IEnumerable<T> phases,
        Func<T, string?> unitProcedure)
    {
        var groups = new List<Isa88UnitGroup<T>>();
        foreach (var phase in phases)
        {
            var name = UnitName(unitProcedure(phase));
            if (groups.Count == 0 || !string.Equals(groups[^1].UnitProcedure, name, StringComparison.Ordinal))
                groups.Add(new Isa88UnitGroup<T>(name, [phase]));
            else
                groups[^1].Phases.Add(phase);
        }

        return groups;
    }
}

public sealed class Isa88UnitGroup<T>
{
    public Isa88UnitGroup(string unitProcedure, List<T> phases)
    {
        UnitProcedure = unitProcedure;
        Phases = phases;
    }

    public string UnitProcedure { get; }
    public List<T> Phases { get; }
}
