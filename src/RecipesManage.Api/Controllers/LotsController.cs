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
    public Task<IReadOnlyList<MaterialLotDto>> List(CancellationToken ct) => lots.ListAsync(ct);

    [HttpPost]
    public Task<MaterialLotDto> Create(CreateMaterialLotRequest request, CancellationToken ct) =>
        lots.CreateReceivedAsync(request, ct);

    [HttpGet("{id:guid}/genealogy")]
    public Task<LotGenealogyDto> Genealogy(Guid id, CancellationToken ct) => lots.GenealogyAsync(id, ct);

    [HttpPost("{id:guid}/split")]
    public Task<MaterialLotDto> Split(Guid id, SplitLotRequest request, CancellationToken ct) =>
        lots.SplitAsync(id, request, ct);
}
