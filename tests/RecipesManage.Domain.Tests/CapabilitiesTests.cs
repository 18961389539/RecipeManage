using System.Reflection;
using RecipesManage.Domain.Identity;
using Xunit;

namespace RecipesManage.Domain.Tests;

/// <summary>
/// 把授权矩阵钉成测试。名单本来只在 <see cref="Capabilities"/> 里写一次，
/// 这里再写一遍是故意的：改权限必须同时改这张表，等于一次显式的评审动作。
/// </summary>
public sealed class CapabilitiesTests
{
    private const UserRole A = UserRole.Admin;
    private const UserRole E = UserRole.ProcessEngineer;
    private const UserRole O = UserRole.Operator;
    private const UserRole S = UserRole.Supervisor;
    private const UserRole Q = UserRole.Quality;

    public static TheoryData<string, UserRole[]> Matrix => new()
    {
        { Capabilities.AdminKey, [A] },
        { Capabilities.RecipeAuthorKey, [E] },
        { Capabilities.RecipeExportKey, [E, Q, S] },
        { Capabilities.BatchOperateKey, [O, S] },
        { Capabilities.BatchSkipKey, [S] },
        { Capabilities.BatchConfirmKey, [O, S, Q] },
        { Capabilities.QualityDispositionKey, [Q] },
        { Capabilities.AlarmAckKey, [O, S, Q] },
        { Capabilities.LotReceiveKey, [A, O, S, Q, E] },
        { Capabilities.LotHandleKey, [A, O, S, Q] },
        { Capabilities.EquipmentAdminKey, [A] },
        { Capabilities.EquipmentOperateKey, [A, O, S] },
        { Capabilities.EquipmentSimulateKey, [O, S] },
        { Capabilities.PhaseLibraryKey, [A, E] },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Each_capability_allows_exactly_the_expected_roles(string key, UserRole[] expected)
    {
        var capability = Assert.Single(Capabilities.All, c => c.Key == key);
        foreach (var role in Enum.GetValues<UserRole>())
            Assert.Equal(expected.Contains(role), capability.Allows(role));
    }

    [Fact]
    public void Matrix_covers_every_registered_capability()
    {
        var covered = Matrix.Select(r => (string)r[0]).ToHashSet();
        Assert.Equal(Capabilities.All.Select(c => c.Key).ToHashSet(), covered);
    }

    [Fact]
    public void Every_capability_field_is_registered_in_All()
    {
        var declared = typeof(Capabilities)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Capability))
            .Select(f => (Capability)f.GetValue(null)!)
            .ToHashSet();

        Assert.Equal(declared, Capabilities.All.ToHashSet());
    }

    [Fact]
    public void Keys_are_unique_and_every_key_const_has_a_capability()
    {
        Assert.Equal(Capabilities.All.Count, Capabilities.All.Select(c => c.Key).Distinct().Count());

        var keyConsts = typeof(Capabilities)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Key", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(keyConsts, Capabilities.All.Select(c => c.Key).ToHashSet());
    }

    [Fact]
    public void Segregation_of_duties_holds()
    {
        // 放行/拒收只有质量；没有任何别的角色（含 Admin）能代签。
        Assert.Equal([Q], Capabilities.QualityDisposition.Roles);
        // 跳步会改变已冻结控制配方的执行路径，只有主管。
        Assert.Equal([S], Capabilities.BatchSkip.Roles);
        // 管理员不碰产线：不能启停批次、不能注入现场故障。
        Assert.False(Capabilities.BatchOperate.Allows(A));
        Assert.False(Capabilities.EquipmentSimulate.Allows(A));
        // 工艺工程师写配方，但不能开批次也不能放行。
        Assert.False(Capabilities.BatchOperate.Allows(E));
        Assert.False(Capabilities.QualityDisposition.Allows(E));
        // 操作员不能改配方、不能配置设备。
        Assert.False(Capabilities.RecipeAuthor.Allows(O));
        Assert.False(Capabilities.EquipmentAdmin.Allows(O));
    }
}
