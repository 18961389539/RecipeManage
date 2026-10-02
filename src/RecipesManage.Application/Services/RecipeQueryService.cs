using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 配方读路径：列表 / 详情 / 版本对比。只依赖数据库——没有当前用户、没有签名守卫，
/// 所以这里不可能夹带写权限判断或写操作（<c>ArchitectureBoundaryTests</c> 钉住）。
/// </summary>
public sealed class RecipeQueryService
{
    private readonly IAppDbContext _db;

    public RecipeQueryService(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<RecipeListItemDto>> ListAsync(CancellationToken ct)
    {
        var recipes = await _db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps)
            .Include(r => r.Versions).ThenInclude(v => v.Approvals)
            .AsSplitQuery()
            .ToListAsync(ct);

        return recipes
            .OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt)
            .Select(r =>
            {
                var approved = r.Versions.FirstOrDefault(v => v.Id == r.CurrentApprovedVersionId);
                var draft = r.Versions.FirstOrDefault(v => v.Id == r.CurrentDraftVersionId);
                var review = r.Versions.FirstOrDefault(v => v.Status == RecipeStatus.InReview);
                var pending = review?.Approvals
                    .Where(a => a.Decision == ApprovalDecision.Pending)
                    .OrderBy(a => a.Seq)
                    .FirstOrDefault();
                var units = RecipeTopology.UnitNamesInProcessOrder(
                    (review ?? approved ?? draft)?.Steps ?? []).ToList();
                return new RecipeListItemDto(
                    r.Id, r.Code, r.Name, r.ProductCode, r.ProductName, r.Lifecycle,
                    approved?.VersionNumber, r.LifecycleStatus,
                    r.UpdatedAt ?? r.CreatedAt, units,
                    pending?.Title,
                    pending?.Meaning,
                    review?.VersionNumber);
            }).ToList();
    }

    public async Task<RecipeDetailDto> GetAsync(Guid id, CancellationToken ct) =>
        RecipeSupport.Map(await _db.LoadAsync(id, ct));

    public async Task<RecipeVersionDiffDto> CompareAsync(Guid id, int fromVersion, int toVersion, CancellationToken ct)
    {
        var recipe = await _db.LoadAsync(id, ct);
        var from = recipe.Versions.SingleOrDefault(v => v.VersionNumber == fromVersion)
                   ?? throw new DomainException("NOT_FOUND", $"找不到版本 v{fromVersion}。");
        var to = recipe.Versions.SingleOrDefault(v => v.VersionNumber == toVersion)
                 ?? throw new DomainException("NOT_FOUND", $"找不到版本 v{toVersion}。");
        var diff = RecipeVersionComparer.Compare(from, to);
        return new RecipeVersionDiffDto(
            diff.FromVersion, diff.ToVersion, diff.AddedSteps, diff.RemovedSteps,
            diff.Changes.Select(c => new RecipeFieldChangeDto(c.Path, c.Before, c.After)).ToList(),
            RecipeVersionComparer.ChangedStepCodes(diff).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
