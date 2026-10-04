using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

public sealed class AuditService(IAppDbContext db, ICurrentUser user)
{
    /// <summary>
    /// 履历查询。<paramref name="sort"/> 取前端的列 prop 名，白名单外（含 null）一律按时间；
    /// <paramref name="dir"/> 只有 "asc" 是升序，其余都是降序——默认"先看最近发生了什么"。
    /// <paramref name="q"/> 是全库关键词（用户名 / 动作码 / 实体 / 详情）。
    /// <paramref name="qTokens"/> 是前端对中文标签（动作、实体两列的显示名）的反查结果，
    /// 逗号分隔的 code——库里没有中文标签，靠它把"按中文搜"扩展到全库而不是只过滤当页。
    /// </summary>
    public async Task<AuditLogPageDto> QueryAsync(
        string? entityType,
        string? entityId,
        string? q,
        string? qTokens,
        int skip,
        int take,
        string? sort,
        string? dir,
        CancellationToken ct)
    {
        // 审计追踪是 GMP 敏感面：第一道门是控制器策略，这里再自保一层——
        // 服务被以后的新入口直接调用时同样只认质量与管理员。
        user.EnsureCan(Capabilities.AuditView, "审计日志仅质量与管理员可查看。");
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        skip = Math.Max(0, skip);
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(x => x.EntityId == entityId);

        // 搜索在 SQL 里做、覆盖全库：以前关键词只过滤当页 50 条，"没搜到"会被读成"没发生过"。
        var like = SearchLike.Normalize(q);
        var tokens = ParseTokens(qTokens);
        if (like is not null || tokens.Length > 0)
            query = query.Where(x =>
                (like != null && (EF.Functions.Like(x.UserName, like, "\\")
                    || EF.Functions.Like(x.Action, like, "\\")
                    || EF.Functions.Like(x.EntityType, like, "\\")
                    || EF.Functions.Like(x.EntityId, like, "\\")
                    || EF.Functions.Like(x.Detail, like, "\\")))
                || tokens.Contains(x.Action) || tokens.Contains(x.EntityType));

        var ascending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        // 排序与分页都在 SQL 里做。以前只能整表读进内存：SQLite 提供器禁止 ORDER BY 原生的
        // DateTimeOffset 列，而 <c>AuditLog.At</c> 现在存成"字典序即时间序"的定宽 TEXT（见 AuditTimestamp），
        // 于是时间列也能直接排。Id 兜底做次级键——同用户名下大量同行时，没有它翻页会重复或漏行。
        var ordered = (sort?.ToLowerInvariant(), ascending) switch
        {
            ("username", true) => query.OrderBy(x => x.UserName).ThenBy(x => x.Id),
            ("username", false) => query.OrderByDescending(x => x.UserName).ThenBy(x => x.Id),
            ("action", true) => query.OrderBy(x => x.Action).ThenBy(x => x.Id),
            ("action", false) => query.OrderByDescending(x => x.Action).ThenBy(x => x.Id),
            ("entitytype", true) => query.OrderBy(x => x.EntityType).ThenBy(x => x.Id),
            ("entitytype", false) => query.OrderByDescending(x => x.EntityType).ThenBy(x => x.Id),
            ("detail", true) => query.OrderBy(x => x.Detail).ThenBy(x => x.Id),
            ("detail", false) => query.OrderByDescending(x => x.Detail).ThenBy(x => x.Id),
            (_, true) => query.OrderBy(x => x.At).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.At).ThenBy(x => x.Id),
        };

        return new AuditLogPageDto(
            await query.CountAsync(ct),
            (await ordered.Skip(skip).Take(take).ToListAsync(ct))
                .Select(x => new AuditLogDto(x.Id, x.UserName, x.Action, x.EntityType, x.EntityId, x.Detail, x.At))
                .ToList());
    }

    /// <summary>中文标签反查来的 code 列表。去重并限 50 个：参数来自查询串，不能拼出超长 IN。</summary>
    private static string[] ParseTokens(string? qTokens) =>
        string.IsNullOrWhiteSpace(qTokens)
            ? []
            : qTokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(50)
                .ToArray();
}
