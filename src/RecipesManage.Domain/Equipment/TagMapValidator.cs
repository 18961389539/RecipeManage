using System.Text.Json;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;

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
        if (map.Params.Count == 0 || map.Params.Count > RecipeParameter.MaxSlots)
            throw new DomainException("TAGMAP", $"Params 槽位地址数必须在 1..{RecipeParameter.MaxSlots}（写参帧宽度上限）。");
        if (map.Params.Any(string.IsNullOrWhiteSpace))
            throw new DomainException("TAGMAP", "Params 槽位地址不能为空。");
        foreach (var (tag, address) in map.Measured)
        {
            if (string.IsNullOrWhiteSpace(tag))
                throw new DomainException("TAGMAP", "实测点名称不能为空。");
            if (string.IsNullOrWhiteSpace(address))
                throw new DomainException("TAGMAP", $"实测点 {tag} 缺少地址。");
        }

        return map;
    }

    /// <summary>
    /// 配方参数声明的实测点在设备点表里不存在时返回缺口。
    /// 开批前调用：静默漏归档会进批记录，必须当场报错。
    /// </summary>
    public static IReadOnlyList<string> MissingMeasuredTags(HandshakeTagMap map, IEnumerable<string?> declaredTags) =>
        declaredTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Where(t => !map.Measured.ContainsKey(t))
            .ToList();

    /// <summary>
    /// 写参帧里超出点表槽数的槽位。点表可以短于 16 槽（现场寄存器不够时必须能配），
    /// 但驱动只会写前 N 槽，配方用到第 N+1 槽就会被静默丢掉。
    /// </summary>
    public static IReadOnlyList<int> SlotsBeyondMap(HandshakeTagMap map, IEnumerable<int> writtenSlots) =>
        writtenSlots
            .Where(slot => slot >= map.Params.Count)
            .Distinct()
            .Order()
            .ToList();
}
