using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 审批链配置（Admin）。改链只影响之后的提交：在审版本的节点在提交那一刻已经冻结成自己的副本，
/// 所以这里不需要"有版本在审就锁住配置"——但每一步都留电子签名与审计，因为它改变的是"以后要谁签"。
/// </summary>
public sealed class ApprovalChainService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EsignGuard _esign;

    public ApprovalChainService(IAppDbContext db, ICurrentUser user, IPasswordHasher passwords)
    {
        _db = db;
        _user = user;
        _esign = new EsignGuard(db, user, passwords);
    }

    public async Task<IReadOnlyList<ApprovalChainDto>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.ApprovalChains.AsNoTracking()
            .OrderBy(c => c.IsDefault ? 0 : 1)
            .ThenBy(c => c.Code)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    /// <summary>新增与修改同一个入口（与相模板、设备类的做法一致）：Id 为空即新增。</summary>
    public async Task<ApprovalChainDto> SaveAsync(SaveApprovalChainRequest request, CancellationToken ct)
    {
        EnsureAdmin();
        await _esign.RequireAsync(request.Password, ct);

        var steps = request.Steps.Select(ToDomain).ToList();
        // 校验在域层做一次就够：链能不能保存的规则与"链本身长什么样"是同一件事。
        var reason = ApprovalChain.Validate(steps);
        if (reason is not null)
            throw new DomainException("APPROVAL_CHAIN", reason);

        var code = request.Code?.Trim() ?? "";
        if (code.Length == 0)
            throw new DomainException("APPROVAL_CHAIN", "审批链编码不能为空。");

        // 认 Id 也认 Code：配置界面带着已知的 Id 改，导入/脚本只报 Code 也能幂等。
        ApprovalChainConfig? existing = null;
        if (request.Id is { } id)
        {
            existing = await _db.ApprovalChains.FirstOrDefaultAsync(c => c.Id == id, ct)
                       ?? throw new DomainException("NOT_FOUND", "要改的审批链不存在。");
        }
        else
        {
            existing = await _db.ApprovalChains.FirstOrDefaultAsync(c => c.Code == code, ct);
        }

        // 新建撞已有编码要报错；改自身编码也算新建占用（两条链不能同名）。
        if (existing is null && await _db.ApprovalChains.AnyAsync(c => c.Code == code, ct))
            throw new DomainException("DUP_CODE", $"审批链编码 {code} 已存在。");
        if (existing is not null && code != existing.Code &&
            await _db.ApprovalChains.AnyAsync(c => c.Code == code && c.Id != existing.Id, ct))
        {
            throw new DomainException("DUP_CODE", $"审批链编码 {code} 已被另一条链占用。");
        }

        if (request.IsDefault)
        {
            var others = await _db.ApprovalChains
                .Where(c => c.IsDefault && (existing == null || c.Id != existing.Id))
                .ToListAsync(ct);
            foreach (var other in others)
                other.UnsetDefault();
        }
        else if (existing?.IsDefault == true)
        {
            throw new DomainException("APPROVAL_CHAIN", "必须有一条默认链；请把默认让给另一条，而不是取消它。");
        }

        if (existing is null)
            _db.ApprovalChains.Add(ApprovalChainConfig.Create(code, request.Name, steps, request.IsDefault, request.Enabled));
        else
        {
            // 停用默认链 = 让没选链的配方下次提交时没有链可走。
            if (!request.Enabled && existing.IsDefault)
                throw new DomainException("APPROVAL_CHAIN", "默认链不能停用；先把默认让给另一条。");
            existing.Apply(code, request.Name, steps, request.IsDefault, request.Enabled);
        }

        await AuditAsync("approval-chain.save", code,
            $"{(request.Enabled ? "启用" : "停用")}｜默认={request.IsDefault}｜{steps.Count} 级：{string.Join("→", steps.Select(s => s.Title))}", ct);
        await _db.SaveChangesAsync(ct);
        return Map(await LoadAsync(code, ct));
    }

    public async Task DeleteAsync(Guid id, string password, CancellationToken ct)
    {
        EnsureAdmin();
        await _esign.RequireAsync(password, ct);
        var chain = await _db.ApprovalChains.FirstOrDefaultAsync(c => c.Id == id, ct)
                    ?? throw new DomainException("NOT_FOUND", "审批链不存在。");
        if (chain.IsDefault)
            throw new DomainException("APPROVAL_CHAIN", "默认链不能删除；先把默认让给另一条。");
        if (await _db.Recipes.AnyAsync(r => r.ApprovalChainCode == chain.Code, ct))
            throw new DomainException("APPROVAL_CHAIN", $"仍有配方在用 {chain.Code}，先给它们改链。");

        _db.ApprovalChains.Remove(chain);
        await AuditAsync("approval-chain.delete", chain.Code, $"删除审批链 {chain.Name}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<ApprovalChainConfig> LoadAsync(string code, CancellationToken ct) =>
        await _db.ApprovalChains.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code, ct)
        ?? throw new DomainException("NOT_FOUND", $"审批链 {code} 保存后读不到。");

    private static ApprovalChainDto Map(ApprovalChainConfig c) =>
        new(c.Id, c.Code, c.Name, c.IsDefault, c.Enabled,
            c.ToChain().Steps.Select(s => new ApprovalChainStepDto(s.Node, s.Title, s.RequiredRole, s.MeaningApproved, s.MeaningRejected)).ToList());

    /// <summary>节点标识由角色派生：管理员配的是"叫什么、谁签、签了什么话"，不是内部标识。</summary>
    private static ApprovalChainStep ToDomain(ApprovalChainStepRequest s) =>
        new(s.RequiredRole.NodeFor(), s.Title ?? "", s.RequiredRole, s.MeaningApproved ?? "", s.MeaningRejected ?? "");

    private void EnsureAdmin() => _esign.EnsureCan(Capabilities.Admin);

    private async Task AuditAsync(string action, string entityId, string? detail, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, action, "ApprovalChain", entityId, detail));
        await Task.CompletedTask;
    }
}
