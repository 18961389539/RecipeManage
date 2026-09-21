using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class EmbeddedPostgresPathTests
{
    [Fact]
    public void ResolveDataDirectory_PrefersConfiguredPath()
    {
        var dir = EmbeddedPostgresRuntime.ResolveDataDirectory(@"D:\brmes-pg", @"C:\stable", @"C:\legacy");
        Assert.Equal(@"D:\brmes-pg", dir);
    }

    [Fact]
    public void ResolveDataDirectory_UsesLegacyTempWhenItHasFiles_DoesNotWipeRunningInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"brmes-pg-path-{Guid.NewGuid():N}");
        var stable = Path.Combine(root, "stable");
        var legacy = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "marker"), "keep");
        try
        {
            var dir = EmbeddedPostgresRuntime.ResolveDataDirectory(null, stable, legacy);
            Assert.Equal(legacy, dir);
            Assert.False(Directory.Exists(stable));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void ResolveDataDirectory_CreatesStableDirWhenNeitherHasData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"brmes-pg-path-{Guid.NewGuid():N}");
        var stable = Path.Combine(root, "stable");
        var legacy = Path.Combine(root, "legacy");
        try
        {
            var dir = EmbeddedPostgresRuntime.ResolveDataDirectory(null, stable, legacy);
            Assert.Equal(stable, dir);
            Assert.True(Directory.Exists(stable));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }
}
