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

    [HttpPost("classes/{classId:guid}/templates")]
    [Authorize(Policy = AuthorizationPolicies.PhaseLibrary)]
    public Task<PhaseTemplateDto> CreateTemplate(Guid classId, UpsertPhaseTemplateRequest request, CancellationToken ct) =>
        equipment.UpsertTemplateAsync(classId, null, request, ct);

    [HttpPut("classes/{classId:guid}/templates/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.PhaseLibrary)]
    public Task<PhaseTemplateDto> UpdateTemplate(
        Guid classId, Guid id, UpsertPhaseTemplateRequest request, CancellationToken ct) =>
        equipment.UpsertTemplateAsync(classId, id, request, ct);

    [HttpDelete("classes/{classId:guid}/templates/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.PhaseLibrary)]
    public Task DeleteTemplate(Guid classId, Guid id, CancellationToken ct) =>
        equipment.DeleteTemplateAsync(classId, id, ct);

    [HttpGet]
    public Task<IReadOnlyList<EquipmentDto>> List(CancellationToken ct) => equipment.ListAsync(ct);

    // 设备与握手点表决定"往哪台 PLC 的哪些地址写什么"，改错一次就是现场误动作。
    // 界面早已按 Admin 隐藏按钮，但接口此前对任何已登录角色敞开（实测 operator 可 POST /api/equipment）。
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.EquipmentAdmin)]
    public Task<EquipmentDto> Create(UpsertEquipmentRequest request, CancellationToken ct) =>
        equipment.UpsertAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.EquipmentAdmin)]
    public Task<EquipmentDto> Update(Guid id, UpsertEquipmentRequest request, CancellationToken ct) =>
        equipment.UpsertAsync(id, request, ct);

    [HttpPost("{id:guid}/validate-tagmap")]
    [Authorize(Policy = AuthorizationPolicies.EquipmentAdmin)]
    public Task<TagMapCheckDto> ValidateTagMap(Guid id, CancellationToken ct) =>
        equipment.ValidateTagMapAsync(id, ct);

    [HttpPost("{id:guid}/inject-fault")]
    [Authorize(Policy = AuthorizationPolicies.EquipmentSimulate)]
    public Task<TagMapCheckDto> InjectFault(Guid id, [FromBody] InjectSimulatorFaultRequest request, CancellationToken ct) =>
        equipment.InjectSimulatorFaultAsync(id, request.Mode, ct);

    [HttpPost("{id:guid}/test-connection")]
    [Authorize(Policy = AuthorizationPolicies.EquipmentOperate)]
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
