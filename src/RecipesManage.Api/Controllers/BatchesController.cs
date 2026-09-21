using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/batches")]
public sealed class BatchesController(BatchService batches, MaterialLotService lots) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BatchListItemDto>> List(CancellationToken ct) => batches.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<BatchDetailDto> Get(Guid id, CancellationToken ct) => batches.GetAsync(id, ct);

    [HttpGet("{id:guid}/samples")]
    public Task<IReadOnlyList<SampleDto>> Samples(Guid id, CancellationToken ct) => batches.SamplesAsync(id, ct);

    [HttpGet("{id:guid}/handshake-log")]
    public Task<IReadOnlyList<HandshakeLogDto>> HandshakeLog(Guid id, CancellationToken ct) =>
        batches.HandshakeLogAsync(id, ct);

    [HttpGet("{id:guid}/snapshot-drift")]
    public Task<IReadOnlyList<SnapshotDriftDto>> SnapshotDrift(Guid id, CancellationToken ct) =>
        batches.SnapshotDriftAsync(id, ct);

    [HttpGet("{id:guid}/record")]
    public Task<BatchRecordDto> Record(Guid id, CancellationToken ct) => batches.RecordAsync(id, ct);

    [HttpGet("{id:guid}/record.pdf")]
    public async Task<IActionResult> RecordPdf(Guid id, CancellationToken ct)
    {
        var pdf = await batches.ExportPdfAsync(id, ct);
        var detail = await batches.GetAsync(id, ct);
        return File(pdf, "application/pdf", $"{detail.BatchNo}-eBR.pdf");
    }

    [HttpGet("{id:guid}/alarms")]
    public Task<IReadOnlyList<ProcessAlarmDto>> Alarms(Guid id, CancellationToken ct) =>
        batches.AlarmsAsync(id, ct);

    [HttpPost]
    public Task<BatchDetailDto> Create(CreateBatchRequest request, CancellationToken ct) =>
        batches.CreateAsync(request, ct);

    [HttpPost("{id:guid}/start")]
    public Task<BatchDetailDto> Start(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.StartAsync(id, request.Password, ct);

    [HttpPost("{id:guid}/abort")]
    public Task<BatchDetailDto> Abort(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.AbortAsync(id, request.Reason ?? "操作员中止", request.Password, ct);

    [HttpPost("{id:guid}/hold")]
    public Task<BatchDetailDto> Hold(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.HoldAsync(id, request.Reason ?? "操作员保持", request.Password, ct);

    [HttpPost("{id:guid}/resume")]
    public Task<BatchDetailDto> Resume(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.ResumeAsync(id, request.Password, ct);

    [HttpPost("{id:guid}/skip")]
    public Task<BatchDetailDto> Skip(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.SkipAsync(id, request.Reason ?? "主管跳步", request.Password, request.StepId, ct);

    [HttpPost("{id:guid}/confirm")]
    public Task<BatchDetailDto> Confirm(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.ConfirmAsync(id, request.Reason ?? "操作员确认", request.Password, request.StepId, ct);

    [HttpPost("{id:guid}/release")]
    public Task<BatchDetailDto> Release(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.ReleaseAsync(id, request.Reason ?? "质量放行", request.Password, ct);

    [HttpPost("{id:guid}/reject-disposition")]
    public Task<BatchDetailDto> RejectDisposition(Guid id, [FromBody] EsignActionRequest request, CancellationToken ct) =>
        batches.RejectDispositionAsync(id, request.Reason ?? "", request.Password, ct);

    [HttpGet("{id:guid}/lab-samples")]
    public Task<IReadOnlyList<LabSampleDto>> LabSamples(Guid id, CancellationToken ct) =>
        lots.SamplesForBatchAsync(id, ct);

    [HttpPost("{id:guid}/lab-samples")]
    public Task<LabSampleDto> CreateLabSample(Guid id, CreateLabSampleRequest request, CancellationToken ct) =>
        lots.CreateSampleAsync(id, request, ct);

    [HttpPost("{id:guid}/lab-samples/{sampleId:guid}/disposition")]
    public Task<LabSampleDto> DisposeLabSample(Guid id, Guid sampleId, [FromBody] LabSampleDispositionRequest request, CancellationToken ct) =>
        lots.DisposeSampleAsync(sampleId, request, ct, id);
}

[Authorize]
[ApiController]
[Route("api/alarms")]
public sealed class AlarmsController(BatchService batches) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ProcessAlarmDto>> List(CancellationToken ct) =>
        batches.AlarmsAsync(null, ct);

    [HttpPost("{id:guid}/ack")]
    public Task<ProcessAlarmDto> Ack(Guid id, CancellationToken ct) =>
        batches.AcknowledgeAlarmAsync(id, ct);
}
