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
}
