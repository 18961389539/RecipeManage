using RecipesManage.Infrastructure.Persistence;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RecipesManage.Execution.Tests;

[CollectionDefinition("EmbeddedPostgres")]
public sealed class EmbeddedPostgresCollection : ICollectionFixture<EmbeddedPostgresFixture>
{
}

public sealed class EmbeddedPostgresFixture : IAsyncLifetime
{
    public EmbeddedPostgresRuntime Runtime { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Runtime = await EmbeddedPostgresRuntime.StartAsync();

    public async Task DisposeAsync()
    {
        if (Runtime is not null)
            await Runtime.DisposeAsync();
    }
}
