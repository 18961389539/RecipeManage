using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/batches")]
public sealed class BatchesController(BatchService batches, BatchQueryService query, MaterialLotService lots) : ControllerBase
{
    [HttpGet]
    public Task<BatchListPageDto> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        [FromQuery] string? q = null,
        [FromQuery] string? status = null,
        [FromQuery] bool onlyLabPending = false,
        CancellationToken ct = default) =>
        query.ListAsync(skip, take, sort, dir, q, status, onlyLabPending, ct);

    [HttpGet("{id:guid}")]
    public Task<BatchDetailDto> Get(Guid id, CancellationToken ct) => query.GetAsync(id, ct);

    [HttpGet("{id:guid}/samples")]
    public Task<SampleSeriesDto> Samples(Guid id, [FromQuery] int maxPoints = 1500, CancellationToken ct = default) =>
        query.SamplesAsync(id, maxPoints, ct);

    [HttpGet("{id:guid}/handshake-log")]
    public Task<HandshakeLogPageDto> HandshakeLog(
        Guid id, [FromQuery] int take = 2000, CancellationToken ct = default) =>
        query.HandshakeLogAsync(id, take, ct);

    [HttpGet("{id:guid}/snapshot-drift")]
    public Task<IReadOnlyList<SnapshotDriftDto>> SnapshotDrift(Guid id, CancellationToken ct) =>
        query.SnapshotDriftAsync(id, ct);

    [HttpGet("{id:guid}/record")]
    public Task<BatchRecordDto> Record(Guid id, CancellationToken ct) => query.RecordAsync(id, ct);

    [HttpGet("{id:guid}/record.pdf")]
    public async Task<IActionResult> RecordPdf(Guid id, CancellationToken ct)
    {
        var pdf = await query.ExportPdfAsync(id, ct);
        var detail = await query.GetAsync(id, ct);
        return File(pdf, "application/pdf", $"{detail.BatchNo}-eBR.pdf");
    }

    [HttpGet("{id:guid}/alarms")]
    public Task<ProcessAlarmPageDto> Alarms(
        Guid id,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        [FromQuery] string? q = null,
        [FromQuery] bool onlyOpen = false,
        CancellationToken ct = default) =>
        query.AlarmsAsync(id, skip, take, sort, dir, q, onlyOpen, ct);

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.BatchOperate)]
    public Task<BatchDetailDto> Create(CreateBatchRequest request, CancellationToken ct) =>
        batches.CreateAsync(request, ct);

    [HttpPost("{id:guid}/start")]
    [Authorize(Policy = AuthorizationPolicies.BatchOperate)]
    public Task<BatchDetailDto> Start(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.StartAsync(id, request.Password, ct);

    [HttpPost("{id:guid}/abort")]
    [Authorize(Policy = AuthorizationPolicies.BatchOperate)]
    public Task<BatchDetailDto> Abort(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.AbortAsync(id, request.Reason ?? "操作员中止", request.Password, ct);

    [HttpPost("{id:guid}/hold")]
    [Authorize(Policy = AuthorizationPolicies.BatchOperate)]
    public Task<BatchDetailDto> Hold(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.HoldAsync(id, request.Reason ?? "操作员保持", request.Password, ct);

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = AuthorizationPolicies.BatchOperate)]
    public Task<BatchDetailDto> Resume(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.ResumeAsync(id, request.Password, ct);

    [HttpPost("{id:guid}/skip")]
    [Authorize(Policy = AuthorizationPolicies.BatchSkip)]
    public Task<BatchDetailDto> Skip(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.SkipAsync(id, request.Reason ?? "主管跳步", request.Password, request.StepId, ct);

    [HttpPost("{id:guid}/confirm")]
    [Authorize(Policy = AuthorizationPolicies.BatchConfirm)]
    public Task<BatchDetailDto> Confirm(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.ConfirmAsync(id, request.Reason ?? "操作员确认", request.Password, request.StepId, ct);

    [HttpPost("{id:guid}/release")]
    [Authorize(Policy = AuthorizationPolicies.QualityDisposition)]
    public Task<BatchDetailDto> Release(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.ReleaseAsync(id, request.Reason ?? "质量放行", request.Password, ct);

    [HttpPost("{id:guid}/reject-disposition")]
    [Authorize(Policy = AuthorizationPolicies.QualityDisposition)]
    public Task<BatchDetailDto> RejectDisposition(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.RejectDispositionAsync(id, request.Reason ?? "", request.Password, ct);

    [HttpGet("{id:guid}/lab-samples")]
    public Task<IReadOnlyList<LabSampleDto>> LabSamples(Guid id, CancellationToken ct) =>
        lots.SamplesForBatchAsync(id, ct);

    [HttpPost("{id:guid}/lab-samples")]
    [Authorize(Policy = AuthorizationPolicies.LotHandle)]
    public Task<LabSampleDto> CreateLabSample(Guid id, CreateLabSampleRequest request, CancellationToken ct) =>
        lots.CreateSampleAsync(id, request, ct);

    [HttpPost("{id:guid}/lab-samples/{sampleId:guid}/disposition")]
    [Authorize(Policy = AuthorizationPolicies.QualityDisposition)]
    public Task<LabSampleDto> DisposeLabSample(Guid id, Guid sampleId, [FromBody] LabSampleDispositionRequest request, CancellationToken ct) =>
        lots.DisposeSampleAsync(sampleId, request, ct, id);
}

[Authorize]
[ApiController]
[Route("api/alarms")]
public sealed class AlarmsController(BatchService batches, BatchQueryService query) : ControllerBase
{
    [HttpGet]
    public Task<ProcessAlarmPageDto> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        [FromQuery] string? q = null,
        [FromQuery] bool onlyOpen = false,
        CancellationToken ct = default) =>
        query.AlarmsAsync(null, skip, take, sort, dir, q, onlyOpen, ct);

    [HttpPost("{id:guid}/ack")]
    [Authorize(Policy = AuthorizationPolicies.AlarmAck)]
    public Task<ProcessAlarmDto> Ack(Guid id, CancellationToken ct) =>
        batches.AcknowledgeAlarmAsync(id, ct);
}
