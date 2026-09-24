using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

public sealed class AuditService(IAppDbContext db)
{
    /// <summary>
    /// 履历查询。<paramref name="sort"/> 取前端的列 prop 名，白名单外（含 null）一律按时间；
    /// <paramref name="dir"/> 只有 "asc" 是升序，其余都是降序——默认"先看最近发生了什么"。
    /// </summary>
    public async Task<AuditLogPageDto> QueryAsync(
        string? entityType,
        string? entityId,
        int skip,
        int take,
        string? sort,
        string? dir,
        CancellationToken ct)
    {
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        skip = Math.Max(0, skip);
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(x => x.EntityId == entityId);

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
}
