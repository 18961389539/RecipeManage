using Microsoft.EntityFrameworkCore;
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
    void InjectSimulatorFault(Guid equipmentId, string mode);
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
