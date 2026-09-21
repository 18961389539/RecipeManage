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
/// 历史演进里这些 Repair* 挂在 <c>DatabaseSeeder</c> 中被**每次进程启动**执行，
/// 等于让后台代码持续改写业务数据；其中 RepairHeatProcessDuration 还会给
/// 已批准配方追加工艺参数且不留审计。现在按 <c>applied_data_fixes</c> 里的键只执行一次，
/// 并且对受控配方的自动改动强制落审计。
/// </summary>
public static class DataFixRunner
{
    private static readonly (string Key, string Description)[] Ordered =
    [
        ("hold-tags", "补齐 Host_Hold / PLC_Held 点表"),
        ("mojibake", "修复历史字段乱码"),
        ("isa88", "补齐 ISA-88 Unit Procedure / Operation"),
        ("heat-duration", "为缺少时长的 Heat 工步补升温时长"),
    ];

    public static async Task ApplyAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        await RunAsync(db, log, "hold-tags", RepairHoldTagsAsync, ct);
        await RunAsync(db, log, "mojibake", RepairMojibakeAsync, ct);
        await RunAsync(db, log, "isa88", RepairIsa88Async, ct);
        await RunAsync(db, log, "heat-duration", RepairHeatProcessDurationAsync, ct);
    }

    /// <summary>已登记过的修复全部跳过，保证幂等。</summary>
    public static async Task MarkAllAppliedAsync(AppDbContext db, string note, CancellationToken ct = default)
    {
        var applied = await db.DataFixes.Select(f => f.Key).ToListAsync(ct);
        foreach (var (key, _) in Ordered)
        {
            if (applied.Contains(key, StringComparer.Ordinal))
                continue;
            db.DataFixes.Add(new AppliedDataFix(key, DateTimeOffset.UtcNow, note));
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> IsAppliedAsync(AppDbContext db, string key, CancellationToken ct) =>
        await db.DataFixes.AnyAsync(f => f.Key == key, ct);

    private static async Task RunAsync(
        AppDbContext db,
        ILogger log,
        string key,
        Func<AppDbContext, ILogger, CancellationToken, Task<bool>> fix,
        CancellationToken ct)
    {
        if (await IsAppliedAsync(db, key, ct))
            return;

        try
        {
            var changed = await fix(db, log, ct);
            log.LogInformation("数据修复 {Key} 执行完成，是否改写数据={Changed}", key, changed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 修复失败不要阻塞启动：记录后会由下次启动重试。
            log.LogError(ex, "数据修复 {Key} 执行失败，将在下次启动重试", key);
            return;
        }

        var note = Ordered.FirstOrDefault(k => k.Key == key).Description;
        db.DataFixes.Add(new AppliedDataFix(key, DateTimeOffset.UtcNow, note));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 已批准 Heat 工步若只有斜率没有时长，补升温时长（不写 PLC），避免仿真把斜率当工艺秒数。
    /// 这是一次性数据修复，且必须对受控配方的自动改动留痕。
    /// </summary>
    private static async Task<bool> RepairHeatProcessDurationAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        var steps = await db.RecipeSteps.Include(s => s.Parameters)
            .Where(s => s.Type == StepType.Heat)
            .ToListAsync(ct);
        var versionIds = steps.Select(s => s.RecipeVersionId).ToHashSet();
        var versions = await db.RecipeVersions
            .Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        var touched = new Dictionary<Guid, List<string>>();
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
            step.AddParameter(new RecipeParameter(slot, "升温时长", "s", seconds, 0.5, 3600, false, false));

            if (versions.TryGetValue(step.RecipeVersionId, out var version) &&
                version.Status != RecipeStatus.Draft)
            {
                if (!touched.TryGetValue(step.RecipeVersionId, out var list))
                    touched[step.RecipeVersionId] = list = [];
                list.Add($"{step.Code}:升温时长={seconds:0.##}s");
            }
        }

        if (touched.Count > 0)
        {
            foreach (var (versionId, changes) in touched)
            {
                var version = versions[versionId];
                db.AuditLogs.Add(new AuditLog(
                    null, "system", "recipe.autofix.heat-duration", "RecipeVersion", versionId.ToString(),
                    $"一次性数据修复：为 {string.Join("、", changes)} 补写升温时长（原缺时长会误用斜率）"));
            }

            log.LogWarning("数据修复 heat-duration 改写了 {Count} 个已发布配方版本，已写入审计日志", touched.Count);
        }

        await db.SaveChangesAsync(ct);
        return touched.Count > 0;

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

    private static async Task<bool> RepairIsa88Async(AppDbContext db, ILogger log, CancellationToken ct)
    {
        var steps = await db.RecipeSteps.ToListAsync(ct);
        foreach (var step in steps)
            step.EnsureIsa88();
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<bool> RepairMojibakeAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        var recipes = await db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .AsSplitQuery()
            .ToListAsync(ct);

        foreach (var recipe in recipes)
        {
            foreach (var version in recipe.Versions)
            foreach (var step in version.Steps)
            foreach (var parameter in step.Parameters)
                parameter.RestoreRateUnitIfCorrupted();

            var source = recipe.Versions.OrderBy(v => v.VersionNumber).FirstOrDefault();
            if (source is null)
                continue;

            var sourceSteps = source.Steps.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
            foreach (var version in recipe.Versions.Where(v => v.Id != source.Id))
            {
                foreach (var step in version.Steps)
                {
                    if (!sourceSteps.TryGetValue(step.Code, out var from))
                        continue;
                    step.RestoreIfMojibake(from.Name, from.Description);
                    var fromParams = from.Parameters.ToDictionary(p => p.SlotIndex);
                    foreach (var parameter in step.Parameters)
                    {
                        if (fromParams.TryGetValue(parameter.SlotIndex, out var original))
                            parameter.RestoreIfMojibake(original.Name, original.EngineeringUnit);
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<bool> RepairHoldTagsAsync(AppDbContext db, ILogger log, CancellationToken ct)
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
            if (equipment.Protocol == PlcProtocol.ModbusTcp &&
                (string.IsNullOrWhiteSpace(map.HostHold) || looksLikeS7))
            {
                var loop = HandshakeTagMap.ModbusLoopback();
                map.HostHold = loop.HostHold;
                map.PlcHeld = loop.PlcHeld;
            }
            else if (equipment.Protocol == PlcProtocol.OpcUa &&
                     (string.IsNullOrWhiteSpace(map.HostHold) || looksLikeS7))
            {
                var loop = HandshakeTagMap.OpcUaLoopback();
                map.HostHold = loop.HostHold;
                map.PlcHeld = loop.PlcHeld;
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
        }

        if (changed)
            await db.SaveChangesAsync(ct);
        return changed;
    }
}
