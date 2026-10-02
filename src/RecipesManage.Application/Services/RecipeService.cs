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

    public RecipeService(IAppDbContext db, ICurrentUser user, EsignGuard esign)
    {
        _db = db;
        _user = user;
        _esign = esign;
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
        Map(await LoadAsync(id, ct));

    public async Task<RecipeDetailDto> CreateAsync(CreateRecipeRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.RecipeAuthor);
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
        EnsureCan(Capabilities.RecipeAuthor);
        var recipe = await LoadAsync(id, ct);
        recipe.UpdateHeader(request.Name, request.ProductCode, request.ProductName, request.Description);
        await AuditAsync("recipe.header", recipe.Id.ToString(), $"{request.Name} {request.ProductCode}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> SaveProcedureAsync(Guid id, SaveProcedureRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.RecipeAuthor);
        await RequireEsignAsync(request.Password, ct);
        if (string.IsNullOrWhiteSpace(request.ChangeReason))
            throw new DomainException("CHANGE_REASON", "保存工艺必须填写变更原因。");
        var recipe = await LoadAsync(id, ct);
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
        await AuditAsync("recipe.procedure.esign", recipe.Id.ToString(),
            $"v{draft.VersionNumber} steps={steps.Count} {ElectronicSignature.ProcedureSave} 原因：{request.ChangeReason.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(await LoadAsync(id, ct));
    }

    public async Task<RecipeDetailDto> SubmitAsync(Guid id, SubmitRecipeRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.RecipeAuthor);
        await RequireEsignAsync(request.Password, ct);
        var recipe = await LoadAsync(id, ct);
        var chain = await ResolveChainAsync(recipe.ApprovalChainCode, ct);
        recipe.RequireDraft().Submit(
            DateTimeOffset.UtcNow,
            chain,
            _user.UserId ?? Guid.Empty,
            _user.DisplayName,
            request.Comment);
        await AuditAsync("recipe.submit.esign", recipe.Id.ToString(),
            $"{chain.Name}:{ApprovalChain.Submission.MeaningFor(ApprovalDecision.Approved)}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    /// <summary>
    /// 解析这份配方该走哪条链。
    /// 表里一条都没有 = 从没配过链（含只按模型建库的测试库），用代码内置的缺省链；
    /// 但配方显式点了某条链而那条链不在（被停用或被删），必须报错而不是悄悄换一条——
    /// 静默换链等于静默换掉"这一版要谁签"。
    /// </summary>
    private async Task<ApprovalChain> ResolveChainAsync(string? code, CancellationToken ct)
    {
        var rows = await _db.ApprovalChains.AsNoTracking()
            .Where(c => c.Enabled)
            .OrderBy(c => c.Code)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return ApprovalChain.Standard;

        if (code is null)
        {
            var fallback = rows.FirstOrDefault(c => c.IsDefault) ?? rows[0];
            return fallback.ToChain();
        }

        var picked = rows.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        if (picked is null)
            throw new DomainException("APPROVAL_CHAIN", $"配方指定的审批链 {code} 不存在或已停用。");
        return picked.ToChain();
    }

    public async Task<RecipeDetailDto> DecideAsync(Guid id, DecideRequest request, CancellationToken ct)
    {
        var recipe = await LoadAsync(id, ct);
        var version = recipe.Versions.SingleOrDefault(v => v.Status == RecipeStatus.InReview)
                      ?? throw new DomainException("NOT_IN_REVIEW", "没有待审核版本。");

        // 要签的是"链上第一个没签的节点"，它自带要求角色——不查当下的链配置，
        // 所以在审版本不受管理员中途改链影响。
        var node = version.HeadNode
                   ?? throw new DomainException("NO_PENDING_NODE", "本版本已无待处理的审核节点。");
        var role = _user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (role != node.RequiredRole)
            throw new DomainException("FORBIDDEN", $"当前审核节点「{node.Title}」需要 {node.RequiredRole} 角色。");

        await RequireEsignAsync(request.Password, ct);

        version.Decide(_user.UserId ?? Guid.Empty, _user.DisplayName, request.Decision, request.Comment, DateTimeOffset.UtcNow);
        if (version.Status == RecipeStatus.Approved)
            recipe.MarkApproved(version);

        await AuditAsync("recipe.decide.esign", recipe.Id.ToString(),
            $"{node.Title}:{request.Decision}:{node.Meaning}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    /// <summary>
    /// 指定本配方走哪条审批链（null = 默认链）。电子签名 + 审计，因为它改变"这份配方要谁签"。
    /// 在审版本不受影响：它的节点早在提交时冻结好了。
    /// </summary>
    public async Task<RecipeDetailDto> SetApprovalChainAsync(Guid id, UseApprovalChainRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.RecipeAuthor);
        await RequireEsignAsync(request.Password, ct);
        var recipe = await LoadAsync(id, ct);

        var code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim();
        if (code is not null)
        {
            var exists = await _db.ApprovalChains.AsNoTracking().AnyAsync(c => c.Code == code, ct);
            if (!exists)
                throw new DomainException("APPROVAL_CHAIN", $"审批链 {code} 不存在。");
        }

        recipe.UseApprovalChain(code);
        await AuditAsync("recipe.chain", recipe.Id.ToString(),
            code is null ? "审批链改回默认链" : $"审批链改为 {code}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(recipe);
    }

    public async Task<RecipeDetailDto> ReopenAsync(Guid id, SubmitRecipeRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.RecipeAuthor);
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
        EnsureCan(Capabilities.RecipeAuthor);
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
        EnsureCan(Capabilities.RecipeExport);
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
        EnsureCan(Capabilities.RecipeAuthor);
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

    private void EnsureCan(Capability capability) => _esign.EnsureCan(capability);

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
