using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 配方审批流：提交 / 审核 / 驳回后重开 / 指定审批链。每一步都是电子签名动作。
/// <c>decide</c> 的合法角色取决于"链上第一个没签的节点"，是运行时才定的，
/// 静态能力表（<see cref="Capabilities"/>）表达不了，所以这条授权留在本服务里、按节点判定。
/// </summary>
public sealed class RecipeApprovalService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EsignGuard _esign;

    public RecipeApprovalService(IAppDbContext db, ICurrentUser user, EsignGuard esign)
    {
        _db = db;
        _user = user;
        _esign = esign;
    }

    public async Task<RecipeDetailDto> SubmitAsync(Guid id, SubmitRecipeRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        await _esign.RequireAsync(request.Password, ct);
        var recipe = await _db.LoadAsync(id, ct);
        var chain = await ResolveChainAsync(recipe.ApprovalChainCode, ct);
        recipe.RequireDraft().Submit(
            DateTimeOffset.UtcNow,
            chain,
            _user.UserId ?? Guid.Empty,
            _user.DisplayName,
            request.Comment);
        RecipeSupport.Audit(_db, _user, "recipe.submit.esign", recipe.Id.ToString(),
            $"{chain.Name}:{ApprovalChain.Submission.MeaningFor(ApprovalDecision.Approved)}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(recipe);
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
        var recipe = await _db.LoadAsync(id, ct);
        var version = recipe.Versions.SingleOrDefault(v => v.Status == RecipeStatus.InReview)
                      ?? throw new DomainException("NOT_IN_REVIEW", "没有待审核版本。");

        // 要签的是"链上第一个没签的节点"，它自带要求角色——不查当下的链配置，
        // 所以在审版本不受管理员中途改链影响。
        var node = version.HeadNode
                   ?? throw new DomainException("NO_PENDING_NODE", "本版本已无待处理的审核节点。");
        var role = _user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (role != node.RequiredRole)
            throw new DomainException("FORBIDDEN", $"当前审核节点「{node.Title}」需要 {node.RequiredRole} 角色。");

        await _esign.RequireAsync(request.Password, ct);

        version.Decide(_user.UserId ?? Guid.Empty, _user.DisplayName, request.Decision, request.Comment, DateTimeOffset.UtcNow);
        if (version.Status == RecipeStatus.Approved)
            recipe.MarkApproved(version);

        RecipeSupport.Audit(_db, _user, "recipe.decide.esign", recipe.Id.ToString(),
            $"{node.Title}:{request.Decision}:{node.Meaning}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(recipe);
    }

    /// <summary>
    /// 指定本配方走哪条审批链（null = 默认链）。电子签名 + 审计，因为它改变"这份配方要谁签"。
    /// 在审版本不受影响：它的节点早在提交时冻结好了。
    /// </summary>
    public async Task<RecipeDetailDto> SetApprovalChainAsync(Guid id, UseApprovalChainRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        await _esign.RequireAsync(request.Password, ct);
        var recipe = await _db.LoadAsync(id, ct);

        var code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim();
        if (code is not null)
        {
            var exists = await _db.ApprovalChains.AsNoTracking().AnyAsync(c => c.Code == code, ct);
            if (!exists)
                throw new DomainException("APPROVAL_CHAIN", $"审批链 {code} 不存在。");
        }

        recipe.UseApprovalChain(code);
        RecipeSupport.Audit(_db, _user, "recipe.chain", recipe.Id.ToString(),
            code is null ? "审批链改回默认链" : $"审批链改为 {code}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(recipe);
    }

    public async Task<RecipeDetailDto> ReopenAsync(Guid id, SubmitRecipeRequest request, CancellationToken ct)
    {
        _esign.EnsureCan(Capabilities.RecipeAuthor);
        await _esign.RequireAsync(request.Password, ct);
        var recipe = await _db.LoadAsync(id, ct);
        var rejected = recipe.Versions.SingleOrDefault(v => v.Status == RecipeStatus.Rejected)
                       ?? throw new DomainException("NOT_REJECTED", "没有被驳回的版本。");
        rejected.ReopenRejected();
        recipe.RestoreDraft(rejected);
        RecipeSupport.Audit(_db, _user, "recipe.reopen.esign", recipe.Id.ToString(), $"v{rejected.VersionNumber}");
        await _db.SaveChangesAsync(ct);
        return RecipeSupport.Map(recipe);
    }
}
