using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/equipment")]
public sealed class EquipmentController(EquipmentService equipment) : ControllerBase
{
    [HttpGet("classes")]
    public Task<IReadOnlyList<EquipmentClassDto>> Classes(CancellationToken ct) => equipment.ClassesAsync(ct);

    [HttpGet]
    public Task<IReadOnlyList<EquipmentDto>> List(CancellationToken ct) => equipment.ListAsync(ct);

    [HttpPost]
    public Task<EquipmentDto> Create(UpsertEquipmentRequest request, CancellationToken ct) =>
        equipment.UpsertAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<EquipmentDto> Update(Guid id, UpsertEquipmentRequest request, CancellationToken ct) =>
        equipment.UpsertAsync(id, request, ct);

    [HttpPost("{id:guid}/validate-tagmap")]
    public Task<TagMapCheckDto> ValidateTagMap(Guid id, CancellationToken ct) =>
        equipment.ValidateTagMapAsync(id, ct);

    [HttpPost("{id:guid}/inject-fault")]
    public Task<TagMapCheckDto> InjectFault(Guid id, [FromBody] InjectSimulatorFaultRequest request, CancellationToken ct) =>
        equipment.InjectSimulatorFaultAsync(id, request.Mode, ct);

    [HttpPost("{id:guid}/test-connection")]
    public Task<ConnectionTestDto> TestConnection(Guid id, CancellationToken ct) =>
        equipment.TestConnectionAsync(id, ct);
}

[Authorize]
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(EquipmentService equipment) : ControllerBase
{
    [HttpGet]
    public Task<DashboardDto> Get(CancellationToken ct) => equipment.DashboardAsync(ct);
}
