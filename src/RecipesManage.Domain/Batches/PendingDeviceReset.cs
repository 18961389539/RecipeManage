using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// "这台设备的握手位还没确认复位过"的持久标记。
///
/// 为什么要落库：中止时批次先定稿为 Aborted、调度器稍后才去复位设备。两者之间崩了、或 PLC 当时连不上
/// （复位失败只记日志），Aborted 是终态，重启不会再碰它，设备上的 Running / Complete / Trigger 就一直留着——
/// 没有任何东西记得"这台设备欠一次复位"。标记与批次定稿在<b>同一次提交</b>里写入，复位<b>确认成功</b>才删除，
/// 所以崩溃、连不上、重启都不会让它丢。
///
/// 每台设备至多一行（唯一索引）：欠的是同一件事，不管欠了几次。
/// </summary>
public sealed class PendingDeviceReset : Entity
{
    public Guid EquipmentId { get; private set; }
    public Guid BatchId { get; private set; }
    public string BatchNo { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;

    private PendingDeviceReset() { }

    public PendingDeviceReset(Guid equipmentId, Guid batchId, string batchNo, string reason)
    {
        EquipmentId = equipmentId;
        BatchId = batchId;
        BatchNo = batchNo;
        Reason = reason;
    }

    /// <summary>同一台设备又欠了一次：只记最近一次的来源。</summary>
    public void Renew(Guid batchId, string batchNo, string reason)
    {
        BatchId = batchId;
        BatchNo = batchNo;
        Reason = reason;
        Touch();
    }
}
