using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class TagMapValidatorTests
{
    [Fact]
    public void Parse_DefaultMap_Succeeds()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new HandshakeTagMap());
        var map = TagMapValidator.Parse(json);
        Assert.Equal(16, map.Params.Count);
        Assert.False(string.IsNullOrWhiteSpace(map.PlcReady));
    }

    [Fact]
    public void Parse_MissingHandshakeBits_Throws()
    {
        var map = new HandshakeTagMap { PlcReady = "", TriggerWrite = "DB10.8.0" };
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        var ex = Assert.Throws<DomainException>(() => TagMapValidator.Parse(json));
        Assert.Equal("TAGMAP", ex.Code);
    }

    [Fact]
    public void Parse_MissingHoldBits_Throws()
    {
        var map = new HandshakeTagMap { HostHold = "", PlcHeld = "DB10.8.6" };
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        var ex = Assert.Throws<DomainException>(() => TagMapValidator.Parse(json));
        Assert.Equal("TAGMAP", ex.Code);
    }

    [Fact]
    public void Parse_NonThermalMeasuredTags_Succeeds()
    {
        var map = new HandshakeTagMap
        {
            Measured = new Dictionary<string, string>
            {
                ["Viscosity"] = "DB10.84",
                ["FlowRate"] = "DB10.88"
            }
        };
        var parsed = TagMapValidator.Parse(System.Text.Json.JsonSerializer.Serialize(map));
        Assert.Equal(2, parsed.Measured.Count);
        Assert.Contains("Viscosity", parsed.Measured.Keys);
        Assert.Contains("FlowRate", parsed.Measured.Keys);
    }

    [Fact]
    public void Parse_FewerThanMaxParameterSlots_Succeeds()
    {
        var map = new HandshakeTagMap { Params = ["DB10.20", "DB10.24"] };
        var parsed = TagMapValidator.Parse(System.Text.Json.JsonSerializer.Serialize(map));
        Assert.Equal(2, parsed.Params.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Parse_ParameterSlotCountOutOfRange_Throws(int count)
    {
        var map = new HandshakeTagMap
        {
            Params = Enumerable.Range(0, count).Select(i => $"DB10.{20 + i * 4}").ToList()
        };
        var ex = Assert.Throws<DomainException>(() => TagMapValidator.Parse(System.Text.Json.JsonSerializer.Serialize(map)));
        Assert.Equal("TAGMAP", ex.Code);
    }

    [Fact]
    public void Parse_BlankParameterSlotOrMeasuredAddress_Throws()
    {
        var blankSlot = new HandshakeTagMap { Params = ["DB10.20", " "] };
        Assert.Equal("TAGMAP", Assert.Throws<DomainException>(() =>
            TagMapValidator.Parse(System.Text.Json.JsonSerializer.Serialize(blankSlot))).Code);

        var blankAddress = new HandshakeTagMap { Measured = new Dictionary<string, string> { ["Viscosity"] = "" } };
        Assert.Equal("TAGMAP", Assert.Throws<DomainException>(() =>
            TagMapValidator.Parse(System.Text.Json.JsonSerializer.Serialize(blankAddress))).Code);
    }

    [Fact]
    public void MissingMeasuredTags_ReportsOnlyUndeclaredPoints()
    {
        var map = new HandshakeTagMap
        {
            Measured = new Dictionary<string, string> { ["Temperature"] = "DB10.84", ["HoldTime"] = "DB10.92" }
        };

        var missing = TagMapValidator.MissingMeasuredTags(
            map, ["Temperature", " Viscosity ", null, "", "Temperature"]);

        Assert.Equal(["Viscosity"], missing);
    }

    [Fact]
    public void SlotsBeyondMap_ReportsOnlySlotsTheMapCannotCarry()
    {
        var map = new HandshakeTagMap { Params = ["DB10.20", "DB10.24", "DB10.28", "DB10.32"] };

        Assert.Empty(TagMapValidator.SlotsBeyondMap(map, [0, 3]));
        Assert.Equal([4, 15], TagMapValidator.SlotsBeyondMap(map, [15, 4, 4]));
        Assert.Empty(TagMapValidator.SlotsBeyondMap(new HandshakeTagMap(), [0, 15]));
    }
}
