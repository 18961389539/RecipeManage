using System.Text.Json;
using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Equipment;

public static class TagMapValidator
{
    public static HandshakeTagMap Parse(string? json)
    {
        HandshakeTagMap map;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            map = JsonSerializer.Deserialize<HandshakeTagMap>(json ?? "", options) ?? new HandshakeTagMap();
        }
        catch (JsonException ex)
        {
            throw new DomainException("TAGMAP", $"点表 JSON 无效：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(map.PlcReady) || string.IsNullOrWhiteSpace(map.TriggerWrite))
            throw new DomainException("TAGMAP", "点表必须包含 PLC_Ready 与 Trigger_Write，禁止无握手地址盲写。");
        if (string.IsNullOrWhiteSpace(map.HostHold) || string.IsNullOrWhiteSpace(map.PlcHeld))
            throw new DomainException("TAGMAP", "点表必须包含 Host_Hold 与 PLC_Held，禁止无应答保持。");
        if (map.Params.Count != 16)
            throw new DomainException("TAGMAP", "Params 必须为 16 个槽位地址。");
        if (!map.Measured.ContainsKey("Temperature") || !map.Measured.ContainsKey("HoldTime"))
            throw new DomainException("TAGMAP", "Measured 至少需要 Temperature 与 HoldTime。");
        return map;
    }
}
