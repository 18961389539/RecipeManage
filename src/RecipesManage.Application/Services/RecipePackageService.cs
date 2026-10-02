using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 配方包的导出 / 导入。导入永远只产出草稿 v1：不覆盖已有主配方，也不带走任何审核记录，
/// 必须在本机重新电子签名审核后才能投产。
/// </summary>
public sealed class RecipePackageService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EsignGuard _esign;

    public RecipePackageService(IAppDbContext db, ICurrentUser user, EsignGuard esign)
    {
        _db = db;
        _user = user;
        _esign = esign;
    }

    public async Task<RecipePackageDto> ExportAsync(CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeExport);
        var recipes = await _db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .Include(r => r.Versions).ThenInclude(v => v.Edges)
            .Include(r => r.Versions).ThenInclude(v => v.Approvals)
            .AsSplitQuery()
            .ToListAsync(ct);
        var details = recipes
            .OrderBy(r => r.Code)
            .Select(RecipeSupport.Map)
            .ToList();
        return new RecipePackageDto(DateTimeOffset.UtcNow, "recipes", details);
    }

    public async Task<RecipeImportResultDto> ImportAsync(RecipePackageDto package, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        var created = 0;
        var skipped = 0;
        var messages = new List<string>();
        foreach (var item in package.Recipes)
        {
            var code = item.Code.Trim().ToUpperInvariant();
            if (await _db.Recipes.AnyAsync(r => r.Code == code, ct))
            {
                skipped++;
                messages.Add($"{code} 已存在，跳过（不覆盖已有主配方）。");
                continue;
            }

            var source = item.Approved
                         ?? item.Draft
                         ?? item.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (source is null || source.Steps.Count == 0)
            {
                skipped++;
                messages.Add($"{code} 没有可导入的工艺内容。");
                continue;
            }

            var recipe = MasterRecipe.Create(
                code, item.Name, item.ProductCode, item.ProductName, item.Description,
                _user.UserId ?? Guid.Empty);
            var draft = recipe.RequireDraft();
            var idMap = new Dictionary<Guid, Guid>();
            var steps = source.Steps.Select(s =>
            {
                var id = Guid.NewGuid();
                idMap[s.Id] = id;
                return new RecipeStep(
                    draft.Id, s.Code, s.Name, s.Type, s.Ordinal, s.CanvasX, s.CanvasY, s.WatchdogSeconds, s.Description,
                    s.Parameters.Select(p => new RecipeParameter(
                        p.SlotIndex, p.Name, p.EngineeringUnit, p.Setpoint, p.Min, p.Max, p.WriteToPlc, p.ArchiveAsQuality,
                        p.ScaleWithBatch, p.Semantic, p.MeasuredTag)),
                    id, s.UnitProcedure, s.Operation, s.PlcProgramId, s.EquipmentClassCode);
            }).ToList();
            var edges = source.Edges
                .Where(e => idMap.ContainsKey(e.FromStepId) && idMap.ContainsKey(e.ToStepId))
                .Select(e => new RecipeEdge(draft.Id, idMap[e.FromStepId], idMap[e.ToStepId]))
                .ToList();
            draft.ReplaceProcedure(steps, edges);
            _db.Recipes.Add(recipe);
            RecipeSupport.Audit(_db, _user, "recipe.import", recipe.Id.ToString(), code);
            created++;
            messages.Add($"{code} 已导入为草稿 v1，须重新电子签名审核后才能投产。");
        }

        await _db.SaveChangesAsync(ct);
        return new RecipeImportResultDto(created, skipped, messages);
    }
}
