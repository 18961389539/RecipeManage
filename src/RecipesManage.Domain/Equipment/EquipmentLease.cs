using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Equipment;

/// <summary>
/// 设备占用租约：一台设备同一时刻只允许存在一行。
/// 靠数据库唯一索引保证排他，替换原先"先查后写"的应用层 TOCTOU 检查 ——
/// 后者在两名操作员同时点启动时会让两个批次写入同一台 PLC。
/// 批次进入终态（完成/中止/放行/拒收）时必须释放租约。
/// </summary>
public sealed class EquipmentLease : Entity
{
    public Guid EquipmentId { get; private set; }
    public string EquipmentCode { get; private set; } = string.Empty;
    public Guid BatchId { get; private set; }
    public string BatchNo { get; private set; } = string.Empty;
    public DateTimeOffset LeasedAt { get; private set; }

    private EquipmentLease() { }

    public EquipmentLease(Guid equipmentId, string equipmentCode, Guid batchId, string batchNo, DateTimeOffset leasedAt)
    {
        EquipmentId = equipmentId;
        EquipmentCode = equipmentCode;
        BatchId = batchId;
        BatchNo = batchNo;
        LeasedAt = leasedAt;
    }
}

public static class EquipmentLeasePolicy
{
    /// <summary>这些状态仍占用设备，租约必须保留。</summary>
    public static bool OccupiesEquipment(Batches.BatchStatus status) =>
        status is Batches.BatchStatus.Queued
             or Batches.BatchStatus.Running
             or Batches.BatchStatus.Held
             or Batches.BatchStatus.Faulted;

    /// <summary>这些状态已脱离设备，租约必须释放。</summary>
    public static bool ReleasesEquipment(Batches.BatchStatus status) =>
        status is Batches.BatchStatus.Completed
             or Batches.BatchStatus.Aborted
             or Batches.BatchStatus.Released
             or Batches.BatchStatus.DispositionRejected;
}
