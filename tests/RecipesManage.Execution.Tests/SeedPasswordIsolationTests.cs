using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 初始口令必须一账号一份。
///
/// 为什么值得单独钉住：本系统的身份凭证就是口令本身（电子签名 = 再输一次登录密码），
/// 而三审链要求"工程师提交 → 主管审 → 质量放行"是不同的人。历史上 ResolveInitialPasswords
/// 在配置了 Seed:AdminPassword 时用 Enumerable.Repeat 把同一个口令发给五个账号——
/// 一个人就能签完三级审核，数据库里看不出破口，审计时才会暴露。
/// </summary>
public sealed class SeedPasswordIsolationTests
{
    [Fact]
    public async Task ConfiguredInitialPassword_GoesToAdminOnly()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();

        await DatabaseSeeder.SeedAsync(
            db, hasher, new SeedOptions(Demo: false, InitialPassword: "Shared@123"), NullLogger.Instance);

        var users = await db.Users.OrderBy(u => u.UserName).ToListAsync();
        Assert.Equal(5, users.Count);
        // 五个哈希两两不同 ⇒ 五个口令不同（bcrypt 每次加盐，同口令也会不同盐，
        // 所以这里必须用 Verify 断言"谁能用 Shared@123 登录"，而不是比哈希）。
        var accepting = users.Where(u => hasher.Verify("Shared@123", u.PasswordHash)).Select(u => u.UserName).ToList();
        Assert.Equal(["admin"], accepting);

        var qa = users.Single(u => u.UserName == "qa");
        Assert.False(hasher.Verify("Shared@123", qa.PasswordHash));
    }

    [Fact]
    public async Task DemoSeed_KeepsPublishedDistinctDemoPasswords()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();

        await DatabaseSeeder.SeedAsync(
            db, hasher, new SeedOptions(Demo: true, InitialPassword: null), NullLogger.Instance);

        var users = await db.Users.ToDictionaryAsync(u => u.UserName, u => u.PasswordHash);
        Assert.True(hasher.Verify("Admin@123", users["admin"]));
        Assert.True(hasher.Verify("Quality@123", users["qa"]));
        Assert.True(hasher.Verify("Operator@123", users["operator"]));
        // e2e 与 README 依赖这组演示口令，改 Demo 分支前先想清楚要一起改什么。
        Assert.False(hasher.Verify("Admin@123", users["qa"]));
    }

    private static AppDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-seed-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
