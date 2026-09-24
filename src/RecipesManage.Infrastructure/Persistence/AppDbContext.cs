using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Persistence;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Plc;

namespace RecipesManage.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<MasterRecipe> Recipes => Set<MasterRecipe>();
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();
    public DbSet<RecipeStep> RecipeSteps => Set<RecipeStep>();
    public DbSet<RecipeEdge> RecipeEdges => Set<RecipeEdge>();
    public DbSet<RecipeParameter> RecipeParameters => Set<RecipeParameter>();
    public DbSet<ApprovalRecord> ApprovalRecords => Set<ApprovalRecord>();
    public DbSet<ApprovalChainConfig> ApprovalChains => Set<ApprovalChainConfig>();
    public DbSet<EquipmentLine> Equipment => Set<EquipmentLine>();
    public DbSet<EquipmentClass> EquipmentClasses => Set<EquipmentClass>();
    public DbSet<PhaseTemplate> PhaseTemplates => Set<PhaseTemplate>();
    public DbSet<ProductionBatch> Batches => Set<ProductionBatch>();
    public DbSet<BatchStepExecution> BatchStepExecutions => Set<BatchStepExecution>();
    public DbSet<ProcessSample> ProcessSamples => Set<ProcessSample>();
    public DbSet<LabSample> LabSamples => Set<LabSample>();
    public DbSet<MaterialLot> MaterialLots => Set<MaterialLot>();
    public DbSet<BatchMaterialUse> BatchMaterialUses => Set<BatchMaterialUse>();
    public DbSet<HandshakeEvent> HandshakeEvents => Set<HandshakeEvent>();
    public DbSet<ProcessAlarm> ProcessAlarms => Set<ProcessAlarm>();
    public DbSet<BatchLane> Lanes => Set<BatchLane>();
    public DbSet<EquipmentLease> EquipmentLeases => Set<EquipmentLease>();
    public DbSet<AppliedDataFix> DataFixes => Set<AppliedDataFix>();
    public DbSet<SchedulerIntent> SchedulerIntents => Set<SchedulerIntent>();

    /// <summary>
    /// 乐观并发：任何实现 <see cref="IConcurrencyStamped"/> 的实体在被写入前轮换自己的戳，
    /// UPDATE 的 WHERE 里携带加载时的旧值，从而把"后写覆盖先写"变成可捕获的
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>。
    /// </summary>
    private void RotateConcurrencyStamps()
    {
        ChangeTracker.DetectChanges();
        var stamped = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified && e.Entity is IConcurrencyStamped)
            .Select(e => (IConcurrencyStamped)e.Entity)
            .ToList();
        foreach (var entity in stamped)
            entity.RotateConcurrencyStamp();

        if (stamped.Count > 0)
            ChangeTracker.DetectChanges();
    }

    public override int SaveChanges()
    {
        RotateConcurrencyStamps();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RotateConcurrencyStamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        RotateConcurrencyStamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RotateConcurrencyStamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        const string jsonType = "TEXT";

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.UserName).IsUnique();
            e.Property(x => x.UserName).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(64);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            // 见 AuditTimestamp：不写这个转换器，ORDER BY / 范围比较会被 SQLite 提供器直接拒绝。
            e.Property(x => x.At).HasConversion(AuditTimestamp.Converter);
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<MasterRecipe>(e =>
        {
            e.ToTable("master_recipes");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.MasterRecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecipeVersion>(e =>
        {
            e.ToTable("recipe_versions");
            e.HasIndex(x => new { x.MasterRecipeId, x.VersionNumber }).IsUnique();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.RecipeVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Edges).WithOne().HasForeignKey(x => x.RecipeVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Approvals).WithOne().HasForeignKey(x => x.RecipeVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecipeStep>(e =>
        {
            e.ToTable("recipe_steps");
            // Code 是快照 ↔ 执行记录 ↔ 漂移比对的连接键，重码会让下游 ToDictionary 直接抛。
            e.HasIndex(x => new { x.RecipeVersionId, x.Code }).IsUnique();
            e.HasMany(x => x.Parameters).WithOne().HasForeignKey(x => x.RecipeStepId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecipeEdge>().ToTable("recipe_edges");
        modelBuilder.Entity<RecipeParameter>().ToTable("recipe_parameters");
        modelBuilder.Entity<ApprovalRecord>(e =>
        {
            e.ToTable("approval_records");
            // Meaning 是从同一行的冻结副本现推的展示值，不是第三份要维护的数据。
            e.Ignore(x => x.Meaning);
            // 一个版本的链上每个顺序只应有一条：Submit/Reopen 都会先 Clear 再整条展开。
            // 口径从"审核级别"换成"链上顺序"，正是为了让一条链能配出任意长度的节点。
            e.HasIndex(x => new { x.RecipeVersionId, x.Seq }).IsUnique();
        });

        modelBuilder.Entity<ApprovalChainConfig>(e =>
        {
            e.ToTable("approval_chains");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.StepsJson).HasColumnType(jsonType);
        });

        modelBuilder.Entity<EquipmentLine>(e =>
        {
            e.ToTable("equipment");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.TagMapJson).HasColumnType(jsonType);
            e.Property(x => x.WatchdogJson).HasColumnType(jsonType);
        });

        modelBuilder.Entity<EquipmentClass>(e =>
        {
            e.ToTable("equipment_classes");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Templates).WithOne().HasForeignKey(x => x.EquipmentClassId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PhaseTemplate>(e =>
        {
            e.ToTable("phase_templates");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.ParametersJson).HasColumnType(jsonType);
        });

        modelBuilder.Entity<ProductionBatch>(e =>
        {
            e.ToTable("production_batches");
            e.HasIndex(x => x.BatchNo).IsUnique();
            e.Property(x => x.ControlRecipeJson).HasColumnType(jsonType);
            // 列表页要按这些时间列做服务端排序分页，所以挂上定宽 UTC 的文本映射（见 AuditTimestamp）。
            // 转换器与 EF 自带映射逐字符一致、历史行偏移本来就是 +00:00，因此不需要回填。
            e.Property(x => x.CreatedAt).HasConversion(AuditTimestamp.Converter);
            e.Property(x => x.StartedAt).HasConversion(AuditTimestamp.Converter);
            e.Property(x => x.CompletedAt).HasConversion(AuditTimestamp.Converter);
            e.Property(x => x.ReleasedAt).HasConversion(AuditTimestamp.Converter);
            e.HasMany(x => x.StepExecutions).WithOne().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Samples).WithOne().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BatchStepExecution>().ToTable("batch_step_executions");
        modelBuilder.Entity<ProcessSample>(e =>
        {
            e.ToTable("process_samples");
            e.HasIndex(x => new { x.BatchId, x.SampledAt });
            // 趋势要"按时间取最近 N 点"，没这个映射就排不了序、只能整批次读回内存（样本每 400ms 一行）。
            e.Property(x => x.SampledAt).HasConversion(AuditTimestamp.Converter);
        });

        modelBuilder.Entity<LabSample>(e =>
        {
            e.ToTable("lab_samples");
            e.HasIndex(x => x.SampleCode).IsUnique();
            e.HasIndex(x => x.BatchId);
            e.Property(x => x.ResultsJson).HasColumnType(jsonType);
        });

        modelBuilder.Entity<MaterialLot>(e =>
        {
            e.ToTable("material_lots");
            e.HasIndex(x => x.LotNumber).IsUnique();
            e.HasIndex(x => x.ParentLotId);
            e.Property(x => x.CreatedAt).HasConversion(AuditTimestamp.Converter);
        });

        modelBuilder.Entity<BatchMaterialUse>(e =>
        {
            e.ToTable("batch_material_uses");
            e.HasIndex(x => new { x.BatchId, x.MaterialLotId, x.Role }).IsUnique();
        });

        modelBuilder.Entity<HandshakeEvent>(e =>
        {
            e.ToTable("handshake_events");
            e.HasIndex(x => x.BatchId);
        });

        modelBuilder.Entity<ProcessAlarm>(e =>
        {
            e.ToTable("process_alarms");
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.RaisedAt);
            // 两个时间列都要能被服务端 ORDER BY：SQLite 提供器拒绝原生 DateTimeOffset 排序。
            e.Property(x => x.RaisedAt).HasConversion(AuditTimestamp.Converter);
            e.Property(x => x.AcknowledgedAt).HasConversion(AuditTimestamp.Converter);
        });

        modelBuilder.Entity<BatchLane>(e =>
        {
            e.ToTable("batch_lanes");
            e.HasIndex(x => new { x.BatchId, x.EquipmentId }).IsUnique();
            e.HasIndex(x => x.BatchId);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            // 结论按字符串存：库里的历史值就是 "Completed" / "Skipped" 这些词，存成整数会让既有批次读不出来。
            e.Property(x => x.Outcome).HasConversion<string>();
        });

        modelBuilder.Entity<EquipmentLease>(e =>
        {
            e.ToTable("equipment_leases");
            // 一台设备同一时刻只允许一行租约 —— 设备排他由数据库保证，而不是应用层先查后写。
            e.HasIndex(x => x.EquipmentId).IsUnique();
            e.HasIndex(x => x.BatchId);
        });

        modelBuilder.Entity<SchedulerIntent>(e =>
        {
            e.ToTable("scheduler_intents");
            e.HasIndex(x => new { x.BatchId, x.Kind }).IsUnique();
            e.HasIndex(x => x.BatchId);
        });

        modelBuilder.Entity<ProductionBatch>(e =>
        {
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        });

        modelBuilder.Entity<BatchStepExecution>(e =>
        {
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            // 与 batch_lanes.Outcome 同一口径：TEXT 存枚举名，历史数据不用回填。
            e.Property(x => x.Outcome).HasConversion<string>();
        });

        modelBuilder.Entity<AppliedDataFix>(e =>
        {
            e.ToTable("applied_data_fixes");
            e.HasIndex(x => x.Key).IsUnique();
        });

        // Client-generated Guids must not be ValueGeneratedOnAdd: EF would treat clones as
        // existing rows and emit UPDATE ... WHERE Id = <new-guid> (0 rows → concurrency 500).
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var id = entityType.FindProperty(nameof(Entity.Id));
            if (id?.ClrType == typeof(Guid))
                id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }
}

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);
    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}

