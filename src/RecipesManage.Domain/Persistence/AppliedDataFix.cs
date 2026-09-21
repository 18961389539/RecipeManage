using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Persistence;

/// <summary>
/// 已执行过的一次性数据修复。历史演进里曾经把 Repair* 逻辑挂在每次进程启动上，
/// 导致修复脚本反复改写业务数据（甚至改写已批准配方）。记录键之后每个修复只跑一次。
/// </summary>
public sealed class AppliedDataFix : Entity
{
    public string Key { get; private set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; private set; }
    public string? Note { get; private set; }

    private AppliedDataFix() { }

    public AppliedDataFix(string key, DateTimeOffset appliedAt, string? note = null)
    {
        Key = key;
        AppliedAt = appliedAt;
        Note = note;
    }
}
