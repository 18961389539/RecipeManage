using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

/// <summary>
/// 审批链配置。读接口对全登录用户开放（审核台要知道每条链长什么样才好解释"还差谁签"），
/// 写接口 Admin + 电子签名——改它等于改"这份配方以后要谁签"。
/// </summary>
[Authorize]
[ApiController]
[Route("api/approval-chains")]
public sealed class ApprovalChainsController(ApprovalChainService chains) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ApprovalChainDto>> List(CancellationToken ct) => chains.ListAsync(ct);

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public Task<ApprovalChainDto> Save(SaveApprovalChainRequest request, CancellationToken ct) =>
        chains.SaveAsync(request, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public Task Delete(Guid id, [FromBody] EsignPassword request, CancellationToken ct) =>
        chains.DeleteAsync(id, request.Password, ct);
}
