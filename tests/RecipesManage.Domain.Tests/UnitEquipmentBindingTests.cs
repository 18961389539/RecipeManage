using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class UnitEquipmentBindingTests
{
    [Fact]
    public void Normalize_AllPrimary_ReturnsNull()
    {
        var primary = Guid.NewGuid();
        var map = UnitEquipmentBinding.Normalize(
            new Dictionary<string, Guid> { ["UP-A"] = primary },
            ["UP-A", "UP-B"],
            primary);
        Assert.Null(map);
    }

    [Fact]
    public void Normalize_MixedEquipment_KeepsDistinctUnits()
    {
        var primary = Guid.NewGuid();
        var quench = Guid.NewGuid();
        var map = UnitEquipmentBinding.Normalize(
            new Dictionary<string, Guid> { ["UP-淬火"] = quench },
            ["UP-固溶", "UP-淬火", "UP-QC"],
            primary);
        Assert.NotNull(map);
        Assert.Equal(primary, map["UP-固溶"]);
        Assert.Equal(quench, map["UP-淬火"]);
        Assert.Equal(primary, map["UP-QC"]);
    }

    [Fact]
    public void Normalize_UnknownUnit_Throws()
    {
        var ex = Assert.Throws<DomainException>(() =>
            UnitEquipmentBinding.Normalize(
                new Dictionary<string, Guid> { ["UP-unknown"] = Guid.NewGuid() },
                ["UP-A"],
                Guid.NewGuid()));
        Assert.Equal("UNIT_EQ", ex.Code);
    }

    [Fact]
    public void AllIds_IncludesPrimaryAndBound()
    {
        var primary = Guid.NewGuid();
        var extra = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            UnitEquipment = new Dictionary<string, Guid> { ["UP-B"] = extra }
        };
        var ids = UnitEquipmentBinding.AllIds(snapshot, primary);
        Assert.Equal(2, ids.Count);
        Assert.Contains(primary, ids);
        Assert.Contains(extra, ids);
    }

    [Fact]
    public void Resolve_FallsBackToPrimary()
    {
        var primary = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot();
        Assert.Equal(primary, UnitEquipmentBinding.Resolve(snapshot, "UP-A", primary));
    }
}

public sealed class ModbusTagMapTests
{
    [Fact]
    public void ModbusLoopback_PassesHandshakeTagValidation()
    {
        var map = HandshakeTagMap.ModbusLoopback();
        Assert.Equal(16, map.Params.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        var parsed = TagMapValidator.Parse(json);
        Assert.Equal("0", parsed.TriggerWrite);
        Assert.Equal("1", parsed.PlcReady);
    }
}

public sealed class OpcUaTagMapTests
{
    [Fact]
    public void OpcUaLoopback_PassesHandshakeTagValidation()
    {
        var map = HandshakeTagMap.OpcUaLoopback();
        Assert.Equal(16, map.Params.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        var parsed = TagMapValidator.Parse(json);
        Assert.Contains("nsu=urn:brmes:handshake", parsed.PlcReady, StringComparison.Ordinal);
        Assert.True(parsed.OpcUaAutoAcceptCertificates);
        Assert.False(parsed.OpcUaUseSecurity);
    }
}
