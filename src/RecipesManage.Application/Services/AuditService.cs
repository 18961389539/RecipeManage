using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

public sealed class AuditService(IAppDbContext db)
{
    public async Task<AuditLogPageDto> QueryAsync(
        string? entityType,
        string? entityId,
        int skip,
        int take,
        CancellationToken ct)
    {
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        skip = Math.Max(0, skip);
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(x => x.EntityId == entityId);

        if (db.SupportsServerDateOrdering)
        {
            // PostgreSQL（生产）：总数与分页全部下推，不再读入筛选后的全部履历。
            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(x => x.At)
                .Skip(skip)
                .Take(take)
                .Select(x => new AuditLogDto(x.Id, x.UserName, x.Action, x.EntityType, x.EntityId, x.Detail, x.At))
                .ToListAsync(ct);
            return new AuditLogPageDto(total, page);
        }

        // SQLite 提供器对 ORDER BY 里的 DateTimeOffset 直接抛 NotSupportedException，
        // 这套迁移同时面向 PostgreSQL，不能靠 raw SQL 打通，因此本地/测试路径仍在客户端排序。
        var rows = (await query.ToListAsync(ct))
            .OrderByDescending(x => x.At)
            .ToList();
        return new AuditLogPageDto(
            rows.Count,
            rows
                .Skip(skip)
                .Take(take)
                .Select(x => new AuditLogDto(x.Id, x.UserName, x.Action, x.EntityType, x.EntityId, x.Detail, x.At))
                .ToList());
    }
}
