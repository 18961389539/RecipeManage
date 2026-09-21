using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

[Collection("EmbeddedPostgres")]
public sealed class LivePostgreSqlTests
{
    private readonly EmbeddedPostgresFixture _pg;

    public LivePostgreSqlTests(EmbeddedPostgresFixture pg) => _pg = pg;

    [Fact(Timeout = 300_000)]
    public async Task Migrate_SeedsAndQueriesControlRecipeJsonb()
    {
        var cs = await _pg.Runtime.EnsureDatabaseAsync("brmes_jsonb");
        Assert.Contains("brmes_jsonb", cs, StringComparison.OrdinalIgnoreCase);
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        RecipesDatabase.Apply(builder, cs, null);
        await using var db = new AppDbContext(builder.Options);

        await SchemaBootstrap.ApplyAsync(db);
        Assert.Contains("Initial", string.Join(",", db.Database.GetAppliedMigrations()));
        Assert.True(await db.Database.CanConnectAsync());

        var jsonType = await ReadColumnUdtAsync(db, "production_batches", "ControlRecipeJson");
        Assert.Equal("jsonb", jsonType);

        await DatabaseSeeder.SeedAsync(db, new BcryptPasswordHasher(),
            new SeedOptions(Demo: true, InitialPassword: null),
            NullLogger<AppDbContext>.Instance);
        Assert.Equal(5, await db.Users.CountAsync());
        Assert.True(await db.Equipment.AnyAsync(e => e.Code == "HT-01"));
        Assert.True(await db.Recipes.AnyAsync(r => r.Code == "AL-HT-T6"));

        var recipeCode = await ReadJsonbTextAsync(db, "production_batches", "ControlRecipeJson", "recipeCode", "missing");
        Assert.Null(recipeCode);

        var approved = await db.Recipes.AsNoTracking()
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .Include(r => r.Versions).ThenInclude(v => v.Edges)
            .SingleAsync(r => r.Code == "AL-HT-T6");
        var version = approved.Versions.Single(v => v.Id == approved.CurrentApprovedVersionId);
        var snapshot = RecipesManage.Domain.Batches.ControlRecipeSnapshotFactory.From(
            approved, version, DateTimeOffset.UtcNow);
        RecipesManage.Domain.Batches.SnapshotIntegrity.Seal(
            snapshot, RecipesManage.Application.Services.BatchService.JsonOptions, out var json);
        var equipment = await db.Equipment.AsNoTracking().SingleAsync(e => e.Code == "HT-01");
        var batch = RecipesManage.Domain.Batches.ProductionBatch.Create(
            "BPG" + Guid.NewGuid().ToString("N")[..10], equipment.Id, snapshot, json, Guid.NewGuid());
        db.Batches.Add(batch);
        await db.SaveChangesAsync();

        var fromJsonb = await ReadJsonbTextAsync(db, "production_batches", "ControlRecipeJson", "recipeCode", batch.BatchNo);
        Assert.Equal("AL-HT-T6", fromJsonb);
        Assert.True(await JsonbContainsRecipeCodeAsync(db, batch.BatchNo));

        var live = await db.Batches.AsNoTracking().SingleAsync(b => b.BatchNo == batch.BatchNo);
        Assert.Equal(equipment.Id, live.EquipmentId);
        Assert.Contains("AL-HT-T6", live.ControlRecipeJson, StringComparison.Ordinal);
        Assert.True(await db.Users.AnyAsync(u => u.IsActive));
    }

    private static async Task<string?> ReadColumnUdtAsync(AppDbContext db, string table, string column)
    {
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            SELECT udt_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND lower(table_name) = lower(@table)
              AND lower(column_name) = lower(@column)
            """;
        var p1 = cmd.CreateParameter();
        p1.ParameterName = "table";
        p1.Value = table;
        cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter();
        p2.ParameterName = "column";
        p2.Value = column;
        cmd.Parameters.Add(p2);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> ReadJsonbTextAsync(AppDbContext db, string table, string column, string key, string batchNo)
    {
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"""
            SELECT "{column}" ->> '{key}'
            FROM "{table}"
            WHERE "BatchNo" = @batch
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = "batch";
        p.Value = batchNo;
        cmd.Parameters.Add(p);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<bool> JsonbContainsRecipeCodeAsync(AppDbContext db, string batchNo)
    {
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            SELECT "ControlRecipeJson" @> '{"recipeCode":"AL-HT-T6"}'::jsonb
            FROM "production_batches"
            WHERE "BatchNo" = @batch
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = "batch";
        p.Value = batchNo;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync();
        return result is true;
    }
}
