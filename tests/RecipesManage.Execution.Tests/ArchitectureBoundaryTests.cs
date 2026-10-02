using System.Reflection;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Execution;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Simulation;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 把分层方向钉成测试。依赖一旦被无意加回去，不会立刻报错，只会让下一次重构变得更难，
/// 所以这里用"程序集不得引用谁"的形式在编译产物上直接断言。
///
/// 期望的依赖方向（箭头 = 允许引用）：
///   Domain  ←  Application  ←  Infrastructure
///                           ←  Execution       （只认契约，不认 EF 实现，也不认仿真）
///                           ←  Simulation      （只认契约）
/// Api 是组合根，什么都可以引用，不在此约束。
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly Assembly Domain = typeof(SnapshotJson).Assembly;
    private static readonly Assembly Application = typeof(BatchService).Assembly;
    private static readonly Assembly Infrastructure = typeof(DatabaseSeeder).Assembly;
    private static readonly Assembly ExecutionAsm = typeof(BatchSchedulerHostedService).Assembly;
    private static readonly Assembly Simulation = typeof(SimulatedPlcRack).Assembly;

    private static HashSet<string> ProjectRefs(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("RecipesManage.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 批次读写拆分的守卫：读路径只依赖数据库 / 当前用户 / 物料服务 / PDF，
    /// 一旦有人为了"顺手"把调度器、租约、密码校验塞进查询服务，读路径就又缠回写路径了。
    /// </summary>
    [Fact]
    public void BatchQueryService_DoesNotDependOnTheWritePath()
    {
        var ctor = Assert.Single(typeof(BatchQueryService).GetConstructors());
        var deps = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();
        Assert.Equal(
            new HashSet<Type>
            {
                typeof(RecipesManage.Application.Contracts.IAppDbContext),
                typeof(RecipesManage.Application.Contracts.ICurrentUser),
                typeof(MaterialLotService),
                typeof(RecipesManage.Application.Contracts.IBatchRecordPdf),
            },
            deps);
    }

    /// <summary>EsignGuard 由容器注入，不在服务构造函数里 new —— 否则签名校验没有替换与测试的接缝。</summary>
    [Fact]
    public void BatchService_TakesEsignGuardAndQueryServiceFromTheContainer()
    {
        var ctor = Assert.Single(typeof(BatchService).GetConstructors());
        var deps = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();
        Assert.Contains(typeof(EsignGuard), deps);
        Assert.Contains(typeof(BatchQueryService), deps);
        Assert.DoesNotContain(typeof(RecipesManage.Application.Contracts.IPasswordHasher), deps);
        Assert.DoesNotContain(typeof(RecipesManage.Application.Contracts.IBatchRecordPdf), deps);
    }

    [Fact]
    public void Domain_ReferencesNoOtherProjectAssembly() =>
        Assert.Empty(ProjectRefs(Domain));

    [Fact]
    public void Application_DependsOnDomainOnly() =>
        Assert.True(ProjectRefs(Application).IsSubsetOf(new HashSet<string> { "RecipesManage.Domain" }));

    [Fact]
    public void Execution_DoesNotKnowInfrastructureOrSimulation()
    {
        var refs = ProjectRefs(ExecutionAsm);
        Assert.DoesNotContain("RecipesManage.Infrastructure", refs);
        Assert.DoesNotContain("RecipesManage.Simulation", refs);
    }

    [Fact]
    public void Infrastructure_DoesNotKnowSimulationOrExecution()
    {
        var refs = ProjectRefs(Infrastructure);
        Assert.DoesNotContain("RecipesManage.Simulation", refs);
        Assert.DoesNotContain("RecipesManage.Execution", refs);
    }

    [Fact]
    public void Simulation_OnlyKnowsTheContracts()
    {
        var refs = ProjectRefs(Simulation);
        Assert.DoesNotContain("RecipesManage.Infrastructure", refs);
        Assert.DoesNotContain("RecipesManage.Execution", refs);
    }

    [Fact]
    public void ProductionAssemblies_DoNotShipTheOpcUaServer()
    {
        // OPC UA Server 只有仿真从站用；它出现在 Infrastructure 的依赖里就说明仿真又漏回生产代码了。
        Assert.DoesNotContain(
            Infrastructure.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Opc.Ua.Server", StringComparison.Ordinal));
        Assert.Contains(
            Simulation.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Opc.Ua.Server", StringComparison.Ordinal));
    }
}
