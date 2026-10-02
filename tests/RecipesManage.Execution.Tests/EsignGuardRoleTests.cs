using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class EsignGuardRoleTests
{
    [Fact]
    public void Admin_CannotMatchUnlistedOpsRoles()
    {
        var guard = new EsignGuard(null!, new StubUser(UserRole.Admin), null!);
        var ex = Assert.Throws<DomainException>(() =>
            guard.EnsureRole(UserRole.Operator, UserRole.Supervisor));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Fact]
    public void Admin_CanWhenListed()
    {
        var guard = new EsignGuard(null!, new StubUser(UserRole.Admin), null!);
        guard.EnsureRole(UserRole.Admin);
    }

    [Fact]
    public void Operator_CanStart()
    {
        var guard = new EsignGuard(null!, new StubUser(UserRole.Operator), null!);
        guard.EnsureRole(UserRole.Operator, UserRole.Supervisor);
    }

    [Fact]
    public void Quality_CannotSkip()
    {
        var guard = new EsignGuard(null!, new StubUser(UserRole.Quality), null!);
        var ex = Assert.Throws<DomainException>(() => guard.EnsureRole(UserRole.Supervisor));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.ProcessEngineer)]
    [InlineData(UserRole.Quality)]
    public void EnsureCan_UsesTheSharedCapabilityList(UserRole role)
    {
        var guard = new EsignGuard(null!, new StubUser(role), null!);
        var ex = Assert.Throws<DomainException>(() => guard.EnsureCan(Capabilities.BatchOperate));
        Assert.Equal("FORBIDDEN", ex.Code);
        Assert.Equal(CapabilityExtensions.DefaultDeniedMessage, ex.Message);
    }

    [Fact]
    public void EnsureCan_KeepsLegacyMessageWhenGiven()
    {
        var user = new StubUser(UserRole.Operator);
        var ex = Assert.Throws<DomainException>(() =>
            user.EnsureCan(Capabilities.EquipmentAdmin, "仅管理员可配置设备与 PLC 点表。"));
        Assert.Equal("仅管理员可配置设备与 PLC 点表。", ex.Message);
    }

    /// <summary>
    /// 以前：注入故障的策略放行 Admin，服务层却拒绝 Admin —— 两份名单不一致。
    /// 现在两层读同一个 <see cref="Capabilities.EquipmentSimulate"/>，这里钉住服务层这一侧。
    /// 校验发生在碰数据库之前，所以不需要真实 DbContext。
    /// </summary>
    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Quality)]
    [InlineData(UserRole.ProcessEngineer)]
    public async Task EquipmentService_InjectFault_RejectsRolesOutsideTheCapability(UserRole role)
    {
        var service = NewEquipmentService(role);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.InjectSimulatorFaultAsync(Guid.NewGuid(), "None", CancellationToken.None));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Theory]
    [InlineData(UserRole.Operator)]
    [InlineData(UserRole.Supervisor)]
    [InlineData(UserRole.Quality)]
    public async Task EquipmentService_ValidateTagMap_IsAdminOnly(UserRole role)
    {
        var service = NewEquipmentService(role);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ValidateTagMapAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Theory]
    [InlineData(UserRole.Quality)]
    [InlineData(UserRole.ProcessEngineer)]
    public async Task EquipmentService_TestConnection_RejectsNonOperators(UserRole role)
    {
        var service = NewEquipmentService(role);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.TestConnectionAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    private static EquipmentService NewEquipmentService(UserRole role) =>
        new(null!, new StubUser(role), null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EquipmentService>.Instance);

    private sealed class StubUser(UserRole role) : ICurrentUser
    {
        public Guid? UserId => Guid.Empty;
        public string UserName => "stub";
        public string DisplayName => "stub";
        public UserRole? Role => role;
        public bool IsAuthenticated => true;
    }
}
