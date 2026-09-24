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

    private sealed class StubUser(UserRole role) : ICurrentUser
    {
        public Guid? UserId => Guid.Empty;
        public string UserName => "stub";
        public string DisplayName => "stub";
        public UserRole? Role => role;
        public bool IsAuthenticated => true;
    }
}
