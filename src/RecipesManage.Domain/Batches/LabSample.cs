using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

public enum LabSampleType
{
    InProcess = 0,
    Final = 1,
    Retain = 2
}

public enum LabSampleDisposition
{
    Pending = 0,
    Pass = 1,
    Fail = 2,
    Void = 3
}

/// <summary>
/// LIMS 风格实验室样品身份。与 ProcessSample（PLC 测点时序）分开，避免趋势曲线把样品判定当成温度点。
/// </summary>
public sealed class LabSample : Entity
{
    public string SampleCode { get; private set; } = string.Empty;
    public Guid BatchId { get; private set; }
    public Guid? MaterialLotId { get; private set; }
    public Guid? ParentSampleId { get; private set; }
    public Guid? StepId { get; private set; }
    public LabSampleType SampleType { get; private set; }
    public LabSampleDisposition Disposition { get; private set; } = LabSampleDisposition.Pending;
    public string? ResultsJson { get; private set; }
    public string TakenBy { get; private set; } = string.Empty;
    public DateTimeOffset TakenAt { get; private set; }
    public string? DispositionBy { get; private set; }
    public DateTimeOffset? DisposedAt { get; private set; }
    public string? Comment { get; private set; }

    private LabSample() { }

    public LabSample(
        string sampleCode,
        Guid batchId,
        LabSampleType sampleType,
        string takenBy,
        DateTimeOffset takenAt,
        Guid? materialLotId = null,
        Guid? parentSampleId = null,
        Guid? stepId = null,
        string? resultsJson = null)
    {
        if (string.IsNullOrWhiteSpace(sampleCode))
            throw new DomainException("SAMPLE_CODE", "样品编号不能为空。");
        SampleCode = sampleCode.Trim();
        BatchId = batchId;
        SampleType = sampleType;
        TakenBy = string.IsNullOrWhiteSpace(takenBy) ? "operator" : takenBy.Trim();
        TakenAt = takenAt;
        MaterialLotId = materialLotId;
        ParentSampleId = parentSampleId;
        StepId = stepId;
        ResultsJson = string.IsNullOrWhiteSpace(resultsJson) ? null : resultsJson.Trim();
    }

    public void RecordDisposition(LabSampleDisposition disposition, string reviewerName, string? comment, DateTimeOffset now)
    {
        if (Disposition is not LabSampleDisposition.Pending)
            throw new DomainException("SAMPLE_DONE", $"样品 {SampleCode} 已判定为 {Disposition}。");
        if (disposition == LabSampleDisposition.Pending)
            throw new DomainException("SAMPLE_DISP", "判定不能仍为待检。");
        if (disposition == LabSampleDisposition.Fail && string.IsNullOrWhiteSpace(comment))
            throw new DomainException("SAMPLE_FAIL", "不合格样品必须填写对照规格的意见。");
        Disposition = disposition;
        DispositionBy = string.IsNullOrWhiteSpace(reviewerName) ? "quality" : reviewerName.Trim();
        DisposedAt = now;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        Touch();
    }
}
