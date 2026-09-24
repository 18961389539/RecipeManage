namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 配方单元规程绑定的设备类。开批时优先选该类设备；调度仍按程序号校验能力。
/// </summary>
public static class RecipeUnitClass
{
    public static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>
    /// 按该单元写 PLC 程序号选最窄匹配的设备类。无写 PLC 工步或没有类能覆盖全部程序时返回 null。
    /// </summary>
    public static string? Infer(
        IEnumerable<(StepType Type, int? PlcProgramId)> steps,
        IReadOnlyList<(string Code, IReadOnlySet<int> Allowed)> classes)
    {
        var programs = steps
            .Where(s => PlcProgram.WritesToPlc(s.Type))
            .Select(s => PlcProgram.Resolve(s.Type, s.PlcProgramId))
            .Distinct()
            .ToArray();
        if (programs.Length == 0 || classes.Count == 0)
            return null;

        var matches = classes
            .Where(c => programs.All(p => c.Allowed.Contains(p)))
            .OrderBy(c => c.Allowed.Count)
            .ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.Count == 0 ? null : Normalize(matches[0].Code);
    }

    /// <summary>
    /// 上位机单元（质检汇合等）没有写 PLC 程序号。仅当所有汇入边来自同一设备类时继承该类。
    /// 双单元并行汇合到质检（炉 + 淬火）则保持空，不强行指定。
    /// </summary>
    public static string? Inherit(
        string hostUnit,
        IEnumerable<(string FromUnit, string ToUnit)> edges,
        IReadOnlyDictionary<string, string> classByUnit)
    {
        var target = Isa88.UnitName(hostUnit);
        var incoming = edges
            .Where(e => string.Equals(Isa88.UnitName(e.ToUnit), target, StringComparison.Ordinal))
            .Select(e => Isa88.UnitName(e.FromUnit))
            .Where(from => !string.Equals(from, target, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(from => classByUnit.TryGetValue(from, out var code) ? Normalize(code) : null)
            .Where(code => code is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return incoming.Count == 1 ? incoming[0] : null;
    }

    /// <summary>
    /// 各单元已声明的设备类；上位机空单元在唯一汇入时补上继承值。已填写的类不覆盖。
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveDeclared(
        IEnumerable<(string Unit, StepType Type, string? Class)> steps,
        IEnumerable<(string FromUnit, string ToUnit)> edges)
    {
        var list = steps
            .Select(s => (Unit: Isa88.UnitName(s.Unit), s.Type, Class: Normalize(s.Class)))
            .ToList();
        var classByUnit = new Dictionary<string, string>(StringComparer.Ordinal);
        var hostOnly = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in list.GroupBy(s => s.Unit, StringComparer.Ordinal))
        {
            if (!group.Any(s => PlcProgram.WritesToPlc(s.Type)))
                hostOnly.Add(group.Key);
            var stamped = group.Select(s => s.Class).FirstOrDefault(c => c is not null);
            if (stamped is not null)
                classByUnit[group.Key] = stamped;
        }

        foreach (var unit in hostOnly)
        {
            if (classByUnit.ContainsKey(unit))
                continue;
            var inherited = Inherit(unit, edges, classByUnit);
            if (inherited is not null)
                classByUnit[unit] = inherited;
        }

        return classByUnit;
    }

    /// <summary>
    /// 未分类与 GENERIC 不互相排斥。声明类与绑定设备类都是具体类且不同则冲突。
    /// </summary>
    public static bool Conflicts(string? declared, string? actual)
    {
        var want = Normalize(declared);
        var have = Normalize(actual);
        if (want is null || have is null)
            return false;
        if (want == "GENERIC" || have == "GENERIC")
            return false;
        return !string.Equals(want, have, StringComparison.Ordinal);
    }
}
