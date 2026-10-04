using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

// 全局审计日志只有质量与管理员可读（能力 audit.view）：服务层 QueryAsync 再自保一层。
[Authorize(Policy = AuthorizationPolicies.AuditView)]
[ApiController]
[Route("api/audit")]
public sealed class AuditController(AuditService audit) : ControllerBase
{
    [HttpGet]
    public Task<AuditLogPageDto> Query(
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] string? q = null,
        [FromQuery] string? qTokens = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        CancellationToken ct = default) =>
        audit.QueryAsync(entityType, entityId, q, qTokens, skip, take, sort, dir, ct);
}
