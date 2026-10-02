using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 配方编辑（写路径的"设计"部分）：建 / 改表头 / 保存工艺 / 升版。
/// 读 → <see cref="RecipeQueryService"/>；审批流 → <see cref="RecipeApprovalService"/>；导入导出 → <see cref="RecipePackageService"/>。
/// </summary>
public sealed class RecipeService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EsignGuard _esign;

    public RecipeService(IAppDbContext db, ICurrentUser user, EsignGuard esign)
    {
        _db = db;
        _user = user;
        _esign = esign;
    }

    public async Task<RecipeDetailDto> CreateAsync(CreateRecipeRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        if (await _db.Recipes.AnyAsync(r => r.Code == request.Code.Trim().ToUpperInvariant(), ct))
            throw new DomainException("DUP_CODE", "配方编码已存在。");

        var recipe = MasterRecipe.Create(
            request.Code, request.Name, request.ProductCode, request.ProductName, request.Description,
            _user.UserId ?? Guid.Empty);
        _db.Recipes.Add(recipe);
        RecipeSupport.Audit(_db, _user, "recipe.create", recipe.Id.ToString(), recipe.Code);
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(recipe);
    }

    public async Task<RecipeDetailDto> UpdateHeaderAsync(Guid id, UpdateRecipeRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        var recipe = await _db.LoadAsync(id, ct);
        recipe.UpdateHeader(request.Name, request.ProductCode, request.ProductName, request.Description);
        RecipeSupport.Audit(_db, _user, "recipe.header", recipe.Id.ToString(), $"{request.Name} {request.ProductCode}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(recipe);
    }

    public async Task<RecipeDetailDto> SaveProcedureAsync(Guid id, SaveProcedureRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        await _esign.RequireAsync(request.Password, ct);
        if (string.IsNullOrWhiteSpace(request.ChangeReason))
            throw new DomainException("CHANGE_REASON", "保存工艺必须填写变更原因。");
        var recipe = await _db.LoadAsync(id, ct);
        var draft = recipe.RequireDraft();
        _db.RecipeParameters.RemoveRange(draft.Steps.SelectMany(s => s.Parameters));
        _db.RecipeSteps.RemoveRange(draft.Steps);
        _db.RecipeEdges.RemoveRange(draft.Edges);

        var byRequestId = request.Steps.ToDictionary(s => s.Id);
        var declared = RecipeUnitClass.ResolveDeclared(
            request.Steps.Select(s => (s.UnitProcedure ?? "", s.Type, s.EquipmentClassCode)),
            request.Edges
                .Where(e => byRequestId.ContainsKey(e.FromStepId) && byRequestId.ContainsKey(e.ToStepId))
                .Select(e => (
                    byRequestId[e.FromStepId].UnitProcedure ?? "",
                    byRequestId[e.ToStepId].UnitProcedure ?? "")));
        var steps = request.Steps.Select(s => new RecipeStep(
            draft.Id,
            s.Code,
            s.Name,
            s.Type,
            s.Ordinal,
            s.CanvasX,
            s.CanvasY,
            s.WatchdogSeconds,
            s.Description,
            s.Parameters.Select(p => new RecipeParameter(
                p.SlotIndex, p.Name, p.EngineeringUnit, p.Setpoint, p.Min, p.Max, p.WriteToPlc, p.ArchiveAsQuality,
                p.ScaleWithBatch, p.Semantic, p.MeasuredTag)),
            s.Id,
            s.UnitProcedure,
            s.Operation,
            s.PlcProgramId,
            RecipeUnitClass.Normalize(s.EquipmentClassCode)
                ?? (declared.TryGetValue(Isa88.UnitName(s.UnitProcedure), out var inherited) ? inherited : null))).ToList();

        var edges = request.Edges.Select(e => new RecipeEdge(draft.Id, e.FromStepId, e.ToStepId)).ToList();
        draft.ReplaceProcedure(steps, edges);
        RecipeSupport.Audit(_db, _user, "recipe.procedure.esign", recipe.Id.ToString(),
            $"v{draft.VersionNumber} steps={steps.Count} {ElectronicSignature.ProcedureSave} 原因：{request.ChangeReason.Trim()}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(await _db.LoadAsync(id, ct));
    }

    public async Task<RecipeDetailDto> NewVersionAsync(Guid id, NewVersionRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        await _esign.RequireAsync(request.Password, ct);
        var recipe = await _db.LoadAsync(id, ct);
        var next = recipe.CreateNextDraft(_user.UserId ?? Guid.Empty, request.ChangeNote);
        _db.RecipeVersions.Add(next);
        RecipeSupport.Audit(_db, _user, "recipe.new-version.esign", recipe.Id.ToString(), $"v{next.VersionNumber} {request.ChangeNote}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(await _db.LoadAsync(id, ct));
    }
}
