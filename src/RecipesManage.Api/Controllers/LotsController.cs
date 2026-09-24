using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/lots")]
public sealed class LotsController(MaterialLotService lots) : ControllerBase
{
    [HttpGet]
    public Task<MaterialLotPageDto> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        [FromQuery] string? q = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default) =>
        lots.ListAsync(skip, take, sort, dir, q, status, ct);

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.LotReceive)]
    public Task<MaterialLotDto> Create(CreateMaterialLotRequest request, CancellationToken ct) =>
        lots.CreateReceivedAsync(request, ct);

    [HttpGet("{id:guid}/genealogy")]
    public Task<LotGenealogyDto> Genealogy(Guid id, CancellationToken ct) => lots.GenealogyAsync(id, ct);

    [HttpPost("{id:guid}/split")]
    [Authorize(Policy = AuthorizationPolicies.LotHandle)]
    public Task<MaterialLotDto> Split(Guid id, SplitLotRequest request, CancellationToken ct) =>
        lots.SplitAsync(id, request, ct);
}
