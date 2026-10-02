using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Persistence;
using RecipesManage.Domain.Recipes;
using RecipesManage.Application.Dtos;

namespace RecipesManage.Application.Contracts;

public interface IAppDbContext
{
    DbSet<AppUser> Users { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SignatureRecord> SignatureRecords { get; }
    DbSet<MasterRecipe> Recipes { get; }
    DbSet<RecipeVersion> RecipeVersions { get; }
    DbSet<RecipeStep> RecipeSteps { get; }
    DbSet<RecipeEdge> RecipeEdges { get; }
    DbSet<RecipeParameter> RecipeParameters { get; }
    DbSet<ApprovalRecord> ApprovalRecords { get; }
    DbSet<ApprovalChainConfig> ApprovalChains { get; }
    DbSet<EquipmentLine> Equipment { get; }
    DbSet<EquipmentClass> EquipmentClasses { get; }
    DbSet<PhaseTemplate> PhaseTemplates { get; }
    DbSet<ProductionBatch> Batches { get; }
    DbSet<BatchStepExecution> BatchStepExecutions { get; }
    DbSet<ProcessSample> ProcessSamples { get; }
    DbSet<LabSample> LabSamples { get; }
    DbSet<MaterialLot> MaterialLots { get; }
    DbSet<BatchMaterialUse> BatchMaterialUses { get; }
    DbSet<HandshakeEvent> HandshakeEvents { get; }
    DbSet<ProcessAlarm> ProcessAlarms { get; }
    DbSet<BatchLane> Lanes { get; }
    DbSet<EquipmentLease> EquipmentLeases { get; }
    DbSet<AppliedDataFix> DataFixes { get; }
    DbSet<SchedulerIntent> SchedulerIntents { get; }

    /// <summary>调度引擎要在乐观并发冲突后逐实体重载，所以必须看得到变更跟踪器。</summary>
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    string UserName { get; }
    string DisplayName { get; }
    UserRole? Role { get; }
    bool IsAuthenticated { get; }
}

public interface IBatchScheduler
{
    ValueTask EnqueueStartAsync(Guid batchId, CancellationToken cancellationToken = default);
    ValueTask EnqueueAbortAsync(Guid batchId, string reason, CancellationToken cancellationToken = default);
    ValueTask EnqueueHoldAsync(Guid batchId, string reason, CancellationToken cancellationToken = default);
    ValueTask EnqueueSkipAsync(Guid batchId, string reason, Guid? stepId = null, CancellationToken cancellationToken = default);
    ValueTask EnqueueConfirmAsync(Guid batchId, string comment, Guid? stepId = null, CancellationToken cancellationToken = default);
}

public interface IPlcHandshakeClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<Domain.Handshake.PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken);
    Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken cancellationToken);
    Task<Domain.Handshake.PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken);
    Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken);
    Task SetHostHoldAsync(bool value, CancellationToken cancellationToken);
    Task ResetCompleteAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken);
}

public interface IPlcDriverFactory
{
    IPlcHandshakeClient Create(EquipmentLine equipment);
}

/// <summary>
/// 给驱动工厂接入额外协议的扩展点。真实协议（S7 / Modbus / OPC UA）写死在工厂里，
/// 仿真协议由仿真项目通过它接进来——生产代码因此不必认识任何仿真类型。
/// </summary>
public interface IPlcDriverProvider
{
    bool Handles(PlcProtocol protocol);
    IPlcHandshakeClient Create(EquipmentLine equipment);
}

/// <summary>
/// 仿真故障注入。刻意不放在 <see cref="IPlcDriverFactory"/> 上：那是生产路径用的接口，
/// 仿真专用的口子不该出现在它上面。没有注册仿真项目时这个服务不存在，注入故障的接口会明确报"未启用"。
/// </summary>
public interface ISimulatorControl
{
    void InjectFault(Guid equipmentId, string mode);
}

public interface IExecutionPublisher
{
    Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default);
}

public interface IBatchRecordPdf
{
    byte[] Render(BatchRecordDto record);
}

public sealed record ExecutionEvent(
    Guid BatchId,
    string Type,
    object Payload);
