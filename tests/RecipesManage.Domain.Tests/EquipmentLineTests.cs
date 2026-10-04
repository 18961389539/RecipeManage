using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class EquipmentLineTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Create_RejectsMissingCode(string? code)
    {
        var error = Assert.Throws<DomainException>(() => Create(code!, "Furnace"));

        Assert.Equal("EQ_CODE_REQUIRED", error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Create_RejectsMissingName(string? name)
    {
        var error = Assert.Throws<DomainException>(() => Create("HT-01", name!));

        Assert.Equal("EQ_NAME_REQUIRED", error.Code);
    }

    [Fact]
    public void Create_TrimsNameAndNormalizesCode()
    {
        var equipment = Create(" ht-01 ", " Furnace ");

        Assert.Equal("HT-01", equipment.Code);
        Assert.Equal("Furnace", equipment.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Update_RejectsMissingNameWithoutMutatingEntity(string? name)
    {
        var equipment = Create("HT-01", "Furnace");

        var error = Assert.Throws<DomainException>(() => equipment.Update(
            name!, PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, true, "{}", null));

        Assert.Equal("EQ_NAME_REQUIRED", error.Code);
        Assert.Equal("Furnace", equipment.Name);
    }

    private static EquipmentLine Create(string code, string name) =>
        new(code, name, PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, "{}", null);
}
