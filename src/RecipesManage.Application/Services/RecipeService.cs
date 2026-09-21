using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

public sealed class RecipeService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EsignGuard _esign;

    public RecipeService(IAppDbContext db, ICurrentUser user, IPasswordHasher passwords)
    {
        _db = db;
        _user = user;
        _esign = new EsignGuard(db, user, passwords);
    }

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
                    .OrderBy(a => a.Level)
                    .FirstOrDefault();
                var units = (review ?? approved ?? draft)?.Steps
                    .Select(s => Isa88.UnitName(s.UnitProcedure))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(u => u, StringComparer.Ordinal)
                    .ToList() ?? [];
                return new RecipeListItemDto(
                    r.Id, r.Code, r.Name, r.ProductCode, r.ProductName, r.Lifecycle,
                    approved?.VersionNumber, draft?.Status ?? approved?.Status,
                    r.UpdatedAt ?? r.CreatedAt, units,
                    pending?.Level,
                    pending is null ? null : ElectronicSignature.Meaning(pending.Level, pending.Decision),
                    review?.VersionNumber);
            }).ToList();
    }

    public async Task<RecipeDetailDto> GetAsync(Guid id, CancellationToken ct) =>
        Map(await LoadAsync(id, ct));

    public async Task<RecipeDetailDto> CreateAsync(CreateRecipeRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
        if (await _db.Recipes.AnyAsync(r => r.Code == request.Code.Trim().ToUpperInvariant(), ct))
            throw new DomainException("DUP_CODE", "配方编码已存在。");

        var recipe = MasterRecipe.Create(
            request.Code, request.Name, request.ProductCode, request.ProductName, request.Description,
            _user.UserId ?? Guid.Empty);
        _db.Recipes.Add(recipe);
        await AuditAsync("recipe.create", recipe.Id.ToString(), recipe.Code, ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> UpdateHeaderAsync(Guid id, UpdateRecipeRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
        var recipe = await LoadAsync(id, ct);
        recipe.UpdateHeader(request.Name, request.ProductCode, request.ProductName, request.Description);
        await AuditAsync("recipe.header", recipe.Id.ToString(), $"{request.Name} {request.ProductCode}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> SaveProcedureAsync(Guid id, SaveProcedureRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
        await RequireEsignAsync(request.Password, ct);
        if (string.IsNullOrWhiteSpace(request.ChangeReason))
            throw new DomainException("CHANGE_REASON", "保存工艺必须填写变更原因。");
        var recipe = await LoadAsync(id, ct);
        var draft = recipe.RequireDraft();
        _db.RecipeParameters.RemoveRange(draft.Steps.SelectMany(s => s.Parameters));
        _db.RecipeSteps.RemoveRange(draft.Steps);
        _db.RecipeEdges.RemoveRange(draft.Edges);

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
                p.ScaleWithBatch)),
            s.Id,
            s.UnitProcedure,
            s.Operation)).ToList();

        var edges = request.Edges.Select(e => new RecipeEdge(draft.Id, e.FromStepId, e.ToStepId)).ToList();
        draft.ReplaceProcedure(steps, edges);
        await AuditAsync("recipe.procedure.esign", recipe.Id.ToString(),
            $"v{draft.VersionNumber} steps={steps.Count} {ElectronicSignature.ProcedureSave} 原因：{request.ChangeReason.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(await LoadAsync(id, ct));
    }

    public async Task<RecipeDetailDto> SubmitAsync(Guid id, SubmitRecipeRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
        await RequireEsignAsync(request.Password, ct);
        var recipe = await LoadAsync(id, ct);
        recipe.RequireDraft().Submit(
            DateTimeOffset.UtcNow,
            _user.UserId ?? Guid.Empty,
            _user.DisplayName,
            request.Comment);
        await AuditAsync("recipe.submit.esign", recipe.Id.ToString(), ElectronicSignature.Meaning(ApprovalLevel.Author, ApprovalDecision.Approved), ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> DecideAsync(Guid id, DecideRequest request, CancellationToken ct)
    {
        var recipe = await LoadAsync(id, ct);
        var version = recipe.Versions.SingleOrDefault(v => v.Status == RecipeStatus.InReview)
                      ?? throw new DomainException("NOT_IN_REVIEW", "没有待审核版本。");

        var pending = version.Approvals.Single(a => a.Decision == ApprovalDecision.Pending);
        var role = _user.Role ?? throw new DomainException("AUTH", "未登录。");
        var expected = pending.Level == ApprovalLevel.Supervisor ? UserRole.Supervisor : UserRole.Quality;
        if (role is not UserRole.Admin && role != expected)
            throw new DomainException("FORBIDDEN", $"当前审核节点需要 {expected} 角色。");

        await RequireEsignAsync(request.Password, ct);

        version.Decide(pending.Level, _user.UserId ?? Guid.Empty, _user.DisplayName, request.Decision, request.Comment, DateTimeOffset.UtcNow);
        if (version.Status == RecipeStatus.Approved)
            recipe.MarkApproved(version);

        await AuditAsync("recipe.decide.esign", recipe.Id.ToString(),
            $"{pending.Level}:{request.Decision}:{ElectronicSignature.Meaning(pending.Level, request.Decision)}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> ReopenAsync(Guid id, SubmitRecipeRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
        await RequireEsignAsync(request.Password, ct);
        var recipe = await LoadAsync(id, ct);
        var rejected = recipe.Versions.SingleOrDefault(v => v.Status == RecipeStatus.Rejected)
                       ?? throw new DomainException("NOT_REJECTED", "没有被驳回的版本。");
        rejected.ReopenRejected();
        recipe.RestoreDraft(rejected);
        await AuditAsync("recipe.reopen.esign", recipe.Id.ToString(), $"v{rejected.VersionNumber}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> NewVersionAsync(Guid id, NewVersionRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
        await RequireEsignAsync(request.Password, ct);
        var recipe = await LoadAsync(id, ct);
        var next = recipe.CreateNextDraft(_user.UserId ?? Guid.Empty, request.ChangeNote);
        _db.RecipeVersions.Add(next);
        await AuditAsync("recipe.new-version.esign", recipe.Id.ToString(), $"v{next.VersionNumber} {request.ChangeNote}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(await LoadAsync(id, ct));
    }

    public async Task<RecipePackageDto> ExportAsync(CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer, UserRole.Quality, UserRole.Supervisor);
        var recipes = await _db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .Include(r => r.Versions).ThenInclude(v => v.Edges)
            .Include(r => r.Versions).ThenInclude(v => v.Approvals)
            .AsSplitQuery()
            .ToListAsync(ct);
        var details = recipes
            .OrderBy(r => r.Code)
            .Select(Map)
            .ToList();
        return new RecipePackageDto(DateTimeOffset.UtcNow, "recipes", details);
    }

    public async Task<RecipeImportResultDto> ImportAsync(RecipePackageDto package, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.ProcessEngineer);
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
                        p.ScaleWithBatch)),
                    id, s.UnitProcedure, s.Operation);
            }).ToList();
            var edges = source.Edges
                .Where(e => idMap.ContainsKey(e.FromStepId) && idMap.ContainsKey(e.ToStepId))
                .Select(e => new RecipeEdge(draft.Id, idMap[e.FromStepId], idMap[e.ToStepId]))
                .ToList();
            draft.ReplaceProcedure(steps, edges);
            _db.Recipes.Add(recipe);
            await AuditAsync("recipe.import", recipe.Id.ToString(), code, ct);
            created++;
            messages.Add($"{code} 已导入为草稿 v1，须重新电子签名审核后才能投产。");
        }

        await _db.SaveChangesAsync(ct);
        return new RecipeImportResultDto(created, skipped, messages);
    }

    public async Task<RecipeVersionDiffDto> CompareAsync(Guid id, int fromVersion, int toVersion, CancellationToken ct)
    {
        var recipe = await LoadAsync(id, ct);
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

    private async Task<MasterRecipe> LoadAsync(Guid id, CancellationToken ct) =>
        await _db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .Include(r => r.Versions).ThenInclude(v => v.Edges)
            .Include(r => r.Versions).ThenInclude(v => v.Approvals)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new DomainException("NOT_FOUND", "配方不存在。");

    private Task RequireEsignAsync(string? password, CancellationToken ct) => _esign.RequireAsync(password, ct);

    private void EnsureRole(params UserRole[] allowed) => _esign.EnsureRole(allowed);

    private async Task AuditAsync(string action, string entityId, string? detail, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, action, "MasterRecipe", entityId, detail));
        await Task.CompletedTask;
    }

    private static RecipeDetailDto Map(MasterRecipe recipe)
    {
        RecipeVersionDto? MapVersion(RecipeVersion? v) =>
            v is null ? null : new RecipeVersionDto(
                v.Id, v.VersionNumber, v.Status, v.ChangeNote, v.SubmittedAt, v.ApprovedAt,
                v.Steps.OrderBy(s => s.Ordinal).Select(s => new StepDto(
                    s.Id, s.Code, s.Name, s.Type, s.Ordinal, s.CanvasX, s.CanvasY, s.WatchdogSeconds, s.Description,
                    s.UnitProcedure, s.Operation,
                    s.Parameters.OrderBy(p => p.SlotIndex).Select(p => new ParameterDto(
                        p.Id, p.SlotIndex, p.Name, p.EngineeringUnit, p.Setpoint, p.Min, p.Max, p.WriteToPlc, p.ArchiveAsQuality,
                        p.ScaleWithBatch)).ToList()
                )).ToList(),
                v.Edges.Select(e => new EdgeDto(e.Id, e.FromStepId, e.ToStepId)).ToList(),
                v.Approvals.OrderBy(a => a.Level).Select(MapApproval).ToList());

        var draft = recipe.Versions.FirstOrDefault(v => v.Id == recipe.CurrentDraftVersionId);
        var approved = recipe.Versions.FirstOrDefault(v => v.Id == recipe.CurrentApprovedVersionId);
        var versions = recipe.Versions
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => MapVersion(v)!)
            .ToList();
        return new RecipeDetailDto(
            recipe.Id, recipe.Code, recipe.Name, recipe.ProductCode, recipe.ProductName, recipe.Description,
            MapVersion(draft), MapVersion(approved), versions);
    }

    public static ApprovalDto MapApproval(ApprovalRecord a) =>
        new(a.Id, a.Level, a.Decision, a.ReviewerName, a.Comment, a.DecidedAt,
            ElectronicSignature.Meaning(a.Level, a.Decision));
}
