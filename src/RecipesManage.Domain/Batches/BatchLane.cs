using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// ISA-88 并行 Unit Procedure 时每条设备握手车道一行。
/// 车道相位属于"单 lane 私有状态"，必须与 <see cref="ProductionBatch"/> 拆表：
/// 否则多 lane 每 100ms 同时 UPDATE 同一行生产批次，既产生写竞争，
/// 又会让"禁止双批盲写"之外的调度写与 HTTP 控制写互相覆盖。
/// </summary>
public sealed class BatchLane : Entity, IConcurrencyStamped
{
    public Guid BatchId { get; private set; }
    public Guid EquipmentId { get; private set; }
    public string EquipmentCode { get; private set; } = string.Empty;
    public string UnitProcedure { get; private set; } = string.Empty;
    public Guid? StepId { get; private set; }
    public string StepCode { get; private set; } = string.Empty;
    public string Phase { get; private set; } = string.Empty;
    public StepOutcome Outcome { get; private set; } = StepOutcome.Pending;
    public Guid ConcurrencyStamp { get; private set; } = Guid.NewGuid();

    private BatchLane() { }

    public BatchLane(Guid batchId, Guid equipmentId, string equipmentCode, string unitProcedure)
    {
        BatchId = batchId;
        EquipmentId = equipmentId;
        EquipmentCode = equipmentCode;
        UnitProcedure = unitProcedure;
        Phase = nameof(Handshake.HandshakePhase.WaitingPlcReady);
    }

    public void RotateConcurrencyStamp() => ConcurrencyStamp = Guid.NewGuid();

    protected override void OnTouch() => RotateConcurrencyStamp();

    /// <summary>
    /// <paramref name="phase"/> 只能是 <see cref="RecipesManage.Domain.Handshake.HandshakePhase"/> 的令牌
    /// 或保持/等待/确认三个叠加态；<paramref name="outcome"/> 是工步结论，与批次列展示串无关。
    /// </summary>
    public void Update(string phase, StepOutcome outcome, Guid? stepId = null, string? stepCode = null)
    {
        Phase = phase;
        Outcome = outcome;
        if (stepId is Guid id)
            StepId = id;
        if (stepCode is { } code)
            StepCode = code;
        Touch();
    }
}
