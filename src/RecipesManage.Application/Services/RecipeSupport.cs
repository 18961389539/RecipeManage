using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 配方四个服务（编辑 / 查询 / 审批 / 导入导出）共用的无状态件：装载、映射、审计。
/// 做成静态是为了让各服务只持有自己真正用到的依赖——查询服务不需要当前用户，
/// 也就谈不上"读路径里夹带写权限判断"。
/// </summary>
internal static class RecipeSupport
{
    public static async Task<MasterRecipe> LoadAsync(this IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .Include(r => r.Versions).ThenInclude(v => v.Edges)
            .Include(r => r.Versions).ThenInclude(v => v.Approvals)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new DomainException("NOT_FOUND", "配方不存在。");

    public static void Audit(IAppDbContext db, ICurrentUser user, string action, string entityId, string? detail) =>
        db.AuditLogs.Add(new AuditLog(user.UserId, user.UserName, action, "MasterRecipe", entityId, detail));

    public static RecipeDetailDto Map(MasterRecipe recipe)
    {
        RecipeVersionDto? MapVersion(RecipeVersion? v) =>
            v is null ? null : new RecipeVersionDto(
                v.Id, v.VersionNumber, v.Status, v.ChangeNote, v.SubmittedAt, v.ApprovedAt,
                v.Steps.OrderBy(s => s.Ordinal).Select(s => new StepDto(
                    s.Id, s.Code, s.Name, s.Type, s.Ordinal, s.CanvasX, s.CanvasY, s.WatchdogSeconds, s.Description,
                    s.UnitProcedure, s.Operation,
                    s.Parameters.OrderBy(p => p.SlotIndex).Select(p => new ParameterDto(
                        p.Id, p.SlotIndex, p.Name, p.EngineeringUnit, p.Setpoint, p.Min, p.Max, p.WriteToPlc, p.ArchiveAsQuality,
                        p.ScaleWithBatch, p.Semantic, p.MeasuredTag)).ToList(),
                    s.PlcProgramId, s.EquipmentClassCode
                )).ToList(),
                v.Edges.Select(e => new EdgeDto(e.Id, e.FromStepId, e.ToStepId)).ToList(),
                v.Approvals.OrderBy(a => a.Seq).Select(MapApproval).ToList());

        var draft = recipe.Versions.FirstOrDefault(v => v.Id == recipe.CurrentDraftVersionId);
        var approved = recipe.Versions.FirstOrDefault(v => v.Id == recipe.CurrentApprovedVersionId);
        var versions = recipe.Versions
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => MapVersion(v)!)
            .ToList();
        return new RecipeDetailDto(
            recipe.Id, recipe.Code, recipe.Name, recipe.ProductCode, recipe.ProductName, recipe.Description,
            MapVersion(draft), MapVersion(approved), versions, recipe.ApprovalChainCode);
    }

    public static ApprovalDto MapApproval(ApprovalRecord a) =>
        new(a.Id, a.Seq, a.Node, a.Title, a.RequiredRole, a.Decision, a.ReviewerName, a.Comment, a.DecidedAt, a.Meaning);
}
