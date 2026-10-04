using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class EquipmentUpsertValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Create_RejectsMissingCodeBeforeParsingTagMap(string? code)
    {
        await using var db = ServiceHarness.OpenDb();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpsertAsync(null, Request(code, "Furnace"), CancellationToken.None));

        Assert.Equal("EQ_CODE_REQUIRED", error.Code);
        Assert.Empty(db.Equipment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Create_RejectsMissingNameBeforeParsingTagMap(string? name)
    {
        await using var db = ServiceHarness.OpenDb();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpsertAsync(null, Request("HT-01", name), CancellationToken.None));

        Assert.Equal("EQ_NAME_REQUIRED", error.Code);
        Assert.Empty(db.Equipment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Update_RejectsMissingNameBeforeLookingUpEquipment(string? name)
    {
        await using var db = ServiceHarness.OpenDb();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpsertAsync(Guid.NewGuid(), Request("HT-01", name), CancellationToken.None));

        Assert.Equal("EQ_NAME_REQUIRED", error.Code);
        Assert.Empty(db.Equipment);
    }

    private static EquipmentService CreateService(AppDbContext db) =>
        new(db, new ServiceHarness.RoleUser(Guid.NewGuid(), UserRole.Admin),
            new PlcDriverFactory(), NullLogger<EquipmentService>.Instance);

    private static UpsertEquipmentRequest Request(string? code, string? name) =>
        new(code!, name!, PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, true,
            "not-json", null, null);
}
