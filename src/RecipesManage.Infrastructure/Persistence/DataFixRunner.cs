using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Persistence;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 一次性数据修复。
///
/// 历史演进里这些 Repair* 挂在 <c>DatabaseSeeder</c> 中被**每次进程启动**执行，
/// 等于让后台代码持续改写业务数据；其中 RepairHeatProcessDuration 还会给
/// 已批准配方追加工艺参数且不留审计。现在按 <c>applied_data_fixes</c> 里的键只执行一次，
/// 并且每一条对受控数据的机器改写都落审计——改完查不到履历，就等于说不清改没改过。
///
/// 各修复不再自己 SaveChanges：数据改写、审计行、"已修过"标记由 <see cref="RunAsync"/> 一次提交，
/// 三者同生同灭，不会出现"改了数据但没登记"从而每次都重跑的情况。
/// </summary>
public static class DataFixRunner
{
    private static readonly (string Key, string Description)[] Ordered =
    [
        ("hold-tags", "补齐 Host_Hold / PLC_Held 点表"),
        ("mojibake", "修复历史字段乱码"),
        ("isa88", "补齐 ISA-88 Unit Procedure / Operation"),
        ("heat-duration", "为缺少时长的 Heat 工步补升温时长"),
        ("plc-program-templates", "补齐自定义 PLC 程序号相模板（冲洗/气缸）"),
        ("recipe-unit-class", "按程序号回填配方单元设备类"),
        ("recipe-unit-class-host", "汇合质检等上位机单元继承汇入边设备类"),
    ];

    public static async Task ApplyAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        await RunAsync(db, log, "hold-tags", RepairHoldTagsAsync, ct);
        await RunAsync(db, log, "mojibake", RepairMojibakeAsync, ct);
        await RunAsync(db, log, "isa88", RepairIsa88Async, ct);
        await RunAsync(db, log, "heat-duration", RepairHeatProcessDurationAsync, ct);
        await RunAsync(db, log, "plc-program-templates", EnsureCustomProgramTemplatesAsync, ct);
        await RunAsync(db, log, "recipe-unit-class", BackfillRecipeUnitClassAsync, ct);
        await RunAsync(db, log, "recipe-unit-class-host", BackfillHostUnitClassAsync, ct);
    }

    private static async Task<bool> IsAppliedAsync(AppDbContext db, string key, CancellationToken ct) =>
        await db.DataFixes.AnyAsync(f => f.Key == key, ct);

    private static async Task RunAsync(
        AppDbContext db,
        ILogger log,
        string key,
        Func<AppDbContext, Trail, CancellationToken, Task<bool>> fix,
        CancellationToken ct)
    {
        if (await IsAppliedAsync(db, key, ct))
            return;

        var trail = new Trail();
        bool changed;
        try
        {
            changed = await fix(db, trail, ct);
            trail.WriteAuditLogs(db, key);
            db.DataFixes.Add(new AppliedDataFix(
                key, DateTimeOffset.UtcNow, Ordered.First(k => k.Key == key).Description));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 修复失败不要阻塞启动：记录后会由下次启动重试。但必须丢掉这个上下文里没提交的改写，
            // 否则它们会被下一条修复的 SaveChanges 一起写库，履历上就成了另一条修复干的。
            db.ChangeTracker.Clear();
            log.LogError(ex, "数据修复 {Key} 执行失败，将在下次启动重试", key);
            return;
        }

        if (trail.Count > 0)
            log.LogWarning("数据修复 {Key} 改写了受控数据，已写入 {Audits} 条审计履历", key, trail.Count);
        else
            log.LogInformation("数据修复 {Key} 执行完成，是否改写数据={Changed}", key, changed);
    }

    /// <summary>
    /// 只取非 Draft 的版本：草稿还不是受控记录，机器改它由设计器下一次保存带签名覆盖；
    /// 已提交/已批准的版本会进入已放行批次的 eBR，必须留下"谁改的、改了什么"。
    /// </summary>
    private static async Task<Dictionary<Guid, RecipeVersion>> ControlledVersionsAsync(
        AppDbContext db,
        IEnumerable<Guid> versionIds,
        CancellationToken ct)
    {
        var ids = versionIds.Distinct().ToList();
        return await db.RecipeVersions
            .Where(v => ids.Contains(v.Id) && v.Status != RecipeStatus.Draft)
            .ToDictionaryAsync(v => v.Id, ct);
    }

    /// <summary>
    /// 一条修复的实际改写清单，按聚合根汇总成审计行。
    /// 明细按"同一聚合根一条履历"拼接，超长截断——启动时批量修上千个工步不该刷爆履历表。
    /// </summary>
    private sealed class Trail
    {
        private const int DetailLimit = 900;

        private sealed record Entry(string EntityType, string EntityId, string? Label, List<string> Details);

        private readonly Dictionary<(string EntityType, string EntityId), Entry> _entries = [];

        public int Count => _entries.Count;

        public void Add(string entityType, Guid entityId, string detail, string? label = null)
        {
            var key = (entityType, entityId.ToString());
            if (!_entries.TryGetValue(key, out var entry))
                _entries[key] = entry = new Entry(entityType, key.Item2, label, []);
            entry.Details.Add(detail);
        }

        /// <summary>草稿版本（不在 <paramref name="controlled"/> 里）不进履历。</summary>
        public void Version(Guid versionId, IReadOnlyDictionary<Guid, RecipeVersion> controlled, string detail)
        {
            if (controlled.TryGetValue(versionId, out var version))
                Add("RecipeVersion", version.Id, detail, $"配方版本 v{version.VersionNumber}");
        }

        public void Version(RecipeVersion version, string detail)
        {
            if (version.Status == RecipeStatus.Draft)
                return;
            Add("RecipeVersion", version.Id, detail, $"配方版本 v{version.VersionNumber}");
        }

        public void WriteAuditLogs(AppDbContext db, string key)
        {
            foreach (var entry in _entries.Values)
            {
                var joined = string.Join("；", entry.Details);
                if (joined.Length > DetailLimit)
                    joined = string.Concat(joined.AsSpan(0, DetailLimit), $"…（共 {entry.Details.Count} 处）");

                db.AuditLogs.Add(new AuditLog(
                    null,
                    "system",
                    $"{AggregateOf(entry.EntityType)}.autofix.{key}",
                    entry.EntityType,
                    entry.EntityId,
                    $"一次性数据修复 {key}：{(entry.Label is null ? "" : entry.Label + " ")}{joined}"));
            }
        }

        /// <summary>action 前缀按聚合根归类，沿用既有 recipe.autofix.heat-duration 的读法。</summary>
        private static string AggregateOf(string entityType) => entityType switch
        {
            "RecipeVersion" => "recipe",
            "EquipmentLine" or "EquipmentClass" or "PhaseTemplate" => "equipment",
            _ => "data",
        };
    }

    /// <summary>
    /// 已批准 Heat 工步若只有斜率没有时长，补升温时长（不写 PLC），避免仿真把斜率当工艺秒数。
    /// 这是一次性数据修复，且必须对受控配方的自动改动留痕。
    /// </summary>
    private static async Task<bool> RepairHeatProcessDurationAsync(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var steps = await db.RecipeSteps.Include(s => s.Parameters)
            .Where(s => s.Type == StepType.Heat)
            .ToListAsync(ct);
        var controlled = await ControlledVersionsAsync(db, steps.Select(s => s.RecipeVersionId), ct);

        var changed = false;
        foreach (var step in steps)
        {
            if (step.Parameters.Any(HasDurationParam))
                continue;
            var used = step.Parameters.Select(p => p.SlotIndex).ToHashSet();
            var slot = Enumerable.Range(0, RecipeParameter.MaxSlots).FirstOrDefault(i => !used.Contains(i));
            if (used.Contains(slot))
                continue;
            var ramp = step.Parameters.FirstOrDefault(p =>
                p.Name.Contains("斜率", StringComparison.Ordinal) ||
                p.Name.Contains("ramp", StringComparison.OrdinalIgnoreCase));
            var seconds = ramp is { Setpoint: > 0.4 and < 120 } ? ramp.Setpoint : 8;
            if (!step.AddParameter(new RecipeParameter(slot, "升温时长", "s", seconds, 0.5, 3600, false, false)))
                continue;

            changed = true;
            trail.Version(step.RecipeVersionId, controlled, $"{step.Code}:升温时长={seconds:0.##}s（原缺时长会误用斜率）");
        }

        return changed;

        static bool HasDurationParam(RecipeParameter p)
        {
            var name = p.Name ?? "";
            var unit = p.EngineeringUnit ?? "";
            if (name.Contains("斜率", StringComparison.Ordinal) || name.Contains("ramp", StringComparison.OrdinalIgnoreCase) || unit.Contains('/'))
                return false;
            return unit.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("sec", StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("min", StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("hr", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("时长", StringComparison.Ordinal) ||
                   name.Contains("时间", StringComparison.Ordinal) ||
                   name.Contains("等待", StringComparison.Ordinal);
        }
    }

    private static async Task<bool> RepairIsa88Async(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var steps = await db.RecipeSteps.ToListAsync(ct);
        var controlled = await ControlledVersionsAsync(db, steps.Select(s => s.RecipeVersionId), ct);

        var changed = false;
        foreach (var step in steps)
        {
            if (!step.EnsureIsa88())
                continue;
            changed = true;
            trail.Version(step.RecipeVersionId, controlled, $"工步 {step.Code} 补单元 {step.UnitProcedure}／操作 {step.Operation}");
        }

        return changed;
    }

    /// <summary>
    /// 只还原相参数里被写坏的**速率单位**（<c>RestoreRateUnitIfCorrupted</c>）。
    ///
    /// 注意键名 mojibake 比实现大：它<strong>不</strong>处理"中文变问号"那种编码丢失。
    /// 那种损坏在库里已不可逆（问号不含原字符信息），只能从入口拒绝——见
    /// <c>Domain/Common/TextIntegrity</c>。键字符串保持不动：改了会让这条修复对所有已部署库重跑一遍。
    /// </summary>
    private static async Task<bool> RepairMojibakeAsync(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var recipes = await db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .AsSplitQuery()
            .ToListAsync(ct);

        var changed = false;
        foreach (var recipe in recipes)
        {
            var source = recipe.Versions.OrderBy(v => v.VersionNumber).FirstOrDefault();
            var sourceSteps = source?.Steps.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);

            foreach (var version in recipe.Versions)
            {
                var restored = new List<string>();

                foreach (var step in version.Steps)
                    foreach (var parameter in step.Parameters)
                        if (parameter.RestoreRateUnitIfCorrupted())
                        {
                            changed = true;
                            restored.Add($"工步 {step.Code} 槽位 {parameter.SlotIndex} 速率单位→{parameter.EngineeringUnit}");
                        }

                if (source is not null && sourceSteps is not null && version.Id != source.Id)
                {
                    foreach (var step in version.Steps)
                    {
                        if (!sourceSteps.TryGetValue(step.Code, out var from))
                            continue;
                        if (step.RestoreIfMojibake(from.Name, from.Description))
                            restored.Add($"工步 {step.Code} 名称／说明取自 v{source.VersionNumber}");

                        var fromParams = from.Parameters.ToDictionary(p => p.SlotIndex);
                        foreach (var parameter in step.Parameters)
                        {
                            if (fromParams.TryGetValue(parameter.SlotIndex, out var original) &&
                                parameter.RestoreIfMojibake(original.Name, original.EngineeringUnit))
                                restored.Add($"工步 {step.Code} 槽位 {parameter.SlotIndex} 名称／单位取自 v{source.VersionNumber}");
                        }
                    }
                }

                if (restored.Count == 0)
                    continue;
                changed = true;
                trail.Version(version, string.Join("；", restored));
            }
        }

        return changed;
    }

    private static async Task<bool> RepairHoldTagsAsync(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var rows = await db.Equipment.ToListAsync(ct);
        var changed = false;
        foreach (var equipment in rows)
        {
            HandshakeTagMap map;
            try
            {
                map = JsonSerializer.Deserialize<HandshakeTagMap>(equipment.TagMapJson,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? new HandshakeTagMap();
            }
            catch
            {
                continue;
            }

            var looksLikeS7 = !string.IsNullOrWhiteSpace(map.HostHold) &&
                              map.HostHold.StartsWith("DB", StringComparison.OrdinalIgnoreCase);
            var wroteTag = "握手点表";
            if (equipment.Protocol == PlcProtocol.ModbusTcp &&
                (string.IsNullOrWhiteSpace(map.HostHold) || looksLikeS7))
            {
                var loop = HandshakeTagMap.ModbusLoopback();
                map.HostHold = loop.HostHold;
                map.PlcHeld = loop.PlcHeld;
                wroteTag += "（Modbus 回环）";
            }
            else if (equipment.Protocol == PlcProtocol.OpcUa &&
                     (string.IsNullOrWhiteSpace(map.HostHold) || looksLikeS7))
            {
                var loop = HandshakeTagMap.OpcUaLoopback();
                map.HostHold = loop.HostHold;
                map.PlcHeld = loop.PlcHeld;
                wroteTag += "（OPC UA 回环）";
            }
            else if (string.IsNullOrWhiteSpace(map.HostHold))
            {
                map.HostHold = "DB10.8.5";
                map.PlcHeld = "DB10.8.6";
            }
            else
                continue;

            equipment.ReplaceTagMap(JsonSerializer.Serialize(map));
            changed = true;
            trail.Add("EquipmentLine", equipment.Id,
                $"{wroteTag}补 Host_Hold={map.HostHold}、PLC_Held={map.PlcHeld}", $"设备 {equipment.Code}");
        }

        return changed;
    }

    /// <summary>
    /// 已有设备类补冲洗/气缸模板，并把写 PLC 模板的空程序号回填为枚举整型。
    /// </summary>
    private static async Task<bool> EnsureCustomProgramTemplatesAsync(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var changed = false;
        var templates = await db.PhaseTemplates.ToListAsync(ct);
        foreach (var template in templates)
        {
            if (template.PlcProgramId is not null || PlcProgram.IsHost(template.StepType))
                continue;
            if (!template.AssignPlcProgramId((int)template.StepType))
                continue;
            changed = true;
            trail.Add("PhaseTemplate", template.Id,
                $"补 PLC 程序号 {template.PlcProgramId}", $"相模板 {template.Code}");
        }

        var classes = await db.EquipmentClasses.Include(c => c.Templates).ToListAsync(ct);
        var process = classes.FirstOrDefault(c => string.Equals(c.Code, "PROCESS", StringComparison.OrdinalIgnoreCase));
        var generic = classes.FirstOrDefault(c => string.Equals(c.Code, "GENERIC", StringComparison.OrdinalIgnoreCase));

        static PhaseParameterSpec Spec(
            int slot, string name, string unit, double sp, double? min, double? max, bool write, bool qc) =>
            new()
            {
                SlotIndex = slot, Name = name, EngineeringUnit = unit, Setpoint = sp,
                Min = min, Max = max, WriteToPlc = write, ArchiveAsQuality = qc
            };

        void AddToEquipmentClass(EquipmentClass? equipmentClass, params (string Code, string Name, StepType StepType, string Operation, int Watchdog, PhaseParameterSpec[] Parameters, int ProgramId)[] additions)
        {
            if (equipmentClass is null)
                return;
            foreach (var addition in additions)
            {
                if (equipmentClass.Templates.Any(t => t.Code == addition.Code))
                    continue;
                var template = equipmentClass.AddTemplate(addition.Code, addition.Name, addition.StepType,
                    addition.Operation, addition.Watchdog, addition.Parameters, addition.ProgramId);
                changed = true;
                trail.Add("EquipmentClass", equipmentClass.Id,
                    $"新增相模板 {template.Code} {template.Name}（程序号 {template.PlcProgramId}）",
                    $"设备类 {equipmentClass.Code}");
            }
        }

        AddToEquipmentClass(process,
            ("PH-RINSE", "水冲洗", StepType.Transfer, "OP-Rinse 水冲洗", 30,
                [
                    Spec(0, "冲洗流量", "L/min", 12, 1, 40, true, false),
                    Spec(1, "冲洗时长", "s", 3, 0.5, 120, true, false)
                ], 21),
            ("PH-CYL", "气缸保压", StepType.Pressure, "OP-Cyl 气缸保压", 30,
                [
                    Spec(0, "气缸压力", "bar", 4, 1, 10, true, true),
                    Spec(1, "保压时长", "s", 3, 0.5, 60, true, false)
                ], 22));

        AddToEquipmentClass(generic,
            ("GEN-PH-RINSE", "水冲洗", StepType.Transfer, "OP-Rinse 水冲洗", 30,
                [
                    Spec(0, "冲洗流量", "L/min", 12, 1, 40, true, false),
                    Spec(1, "冲洗时长", "s", 3, 0.5, 120, true, false)
                ], 21),
            ("GEN-PH-CYL", "气缸保压", StepType.Pressure, "OP-Cyl 气缸保压", 30,
                [
                    Spec(0, "气缸压力", "bar", 4, 1, 10, true, true),
                    Spec(1, "保压时长", "s", 3, 0.5, 60, true, false)
                ], 22));

        return changed;
    }

    /// <summary>
    /// 已有工步按单元写 PLC 程序号回填最窄匹配的设备类，设计器重开后不再把 PROCESS 推断成 FURNACE。
    /// </summary>
    private static async Task<bool> BackfillRecipeUnitClassAsync(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var classes = await db.EquipmentClasses.Include(c => c.Templates).ToListAsync(ct);
        if (classes.Count == 0)
            return false;
        var catalog = classes
            .Select(c => (c.Code, (IReadOnlySet<int>)c.AllowedProgramIds()))
            .ToList();
        var steps = await db.RecipeSteps.ToListAsync(ct);
        var controlled = await ControlledVersionsAsync(db, steps.Select(s => s.RecipeVersionId), ct);
        var changed = false;
        foreach (var group in steps.GroupBy(s => Isa88.UnitName(s.UnitProcedure), StringComparer.Ordinal))
        {
            var inferred = RecipeUnitClass.Infer(
                group.Select(s => (s.Type, s.PlcProgramId)),
                catalog);
            if (inferred is null)
                continue;
            foreach (var step in group)
            {
                if (!string.IsNullOrWhiteSpace(step.EquipmentClassCode))
                    continue;
                if (!step.AssignEquipmentClass(inferred))
                    continue;
                changed = true;
                trail.Version(step.RecipeVersionId, controlled, $"工步 {step.Code} 按程序号回填设备类 {inferred}");
            }
        }

        return changed;
    }

    /// <summary>
    /// 质检汇合等无写 PLC 工步的单元，从唯一汇入边继承设备类。
    /// </summary>
    private static async Task<bool> BackfillHostUnitClassAsync(AppDbContext db, Trail trail, CancellationToken ct)
    {
        var steps = await db.RecipeSteps.ToListAsync(ct);
        var edges = await db.RecipeEdges.ToListAsync(ct);
        var byId = steps.ToDictionary(s => s.Id);
        var controlled = await ControlledVersionsAsync(db, steps.Select(s => s.RecipeVersionId), ct);
        var changed = false;
        foreach (var versionId in steps.Select(s => s.RecipeVersionId).Distinct())
        {
            var versionSteps = steps.Where(s => s.RecipeVersionId == versionId).ToList();
            var classByUnit = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in versionSteps.GroupBy(s => Isa88.UnitName(s.UnitProcedure), StringComparer.Ordinal))
            {
                var stamped = group.Select(s => RecipeUnitClass.Normalize(s.EquipmentClassCode)).FirstOrDefault(c => c is not null);
                if (stamped is not null)
                    classByUnit[group.Key] = stamped;
            }

            var versionEdges = edges
                .Where(e => e.RecipeVersionId == versionId)
                .Select(e => (
                    FromUnit: byId.TryGetValue(e.FromStepId, out var from) ? Isa88.UnitName(from.UnitProcedure) : "",
                    ToUnit: byId.TryGetValue(e.ToStepId, out var to) ? Isa88.UnitName(to.UnitProcedure) : ""))
                .Where(e => e.FromUnit.Length > 0 && e.ToUnit.Length > 0)
                .ToList();

            foreach (var group in versionSteps.GroupBy(s => Isa88.UnitName(s.UnitProcedure), StringComparer.Ordinal))
            {
                if (group.Any(s => PlcProgram.WritesToPlc(s.Type)))
                    continue;
                if (group.Any(s => !string.IsNullOrWhiteSpace(s.EquipmentClassCode)))
                    continue;
                var inherited = RecipeUnitClass.Inherit(group.Key, versionEdges, classByUnit);
                if (inherited is null)
                    continue;
                foreach (var step in group)
                {
                    if (!step.AssignEquipmentClass(inherited))
                        continue;
                    changed = true;
                    trail.Version(step.RecipeVersionId, controlled, $"工步 {step.Code} 自汇入边继承设备类 {inherited}");
                }
            }
        }

        return changed;
    }
}
