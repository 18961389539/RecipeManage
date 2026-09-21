using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/audit")]
public sealed class AuditController(AuditService audit) : ControllerBase
{
    [HttpGet]
    public Task<AuditLogPageDto> Query(
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default) =>
        audit.QueryAsync(entityType, entityId, skip, take, ct);
}
