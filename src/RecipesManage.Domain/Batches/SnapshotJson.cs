using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 控制配方快照的序列化口径。
///
/// 为什么在域层：快照的完整性哈希（<see cref="SnapshotIntegrity"/>）就是对这套序列化结果取的，
/// 选项一变，已封存批次的哈希就对不上。这样一件"改了就会让历史记录作废"的东西，
/// 以前挂在应用服务 <c>BatchService</c> 上，调度引擎、租约服务、设备服务都得依赖一个业务服务才能读快照。
/// 现在它与快照类型同层，谁都可以依赖，而不必反过来依赖业务流程。
///
/// 这里的选项不能随手改（含命名策略、转义器、枚举转换器）；要改必须配快照 schema 版本。
/// </summary>
public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ControlRecipeSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<ControlRecipeSnapshot>(json, Options);

    /// <summary>批次绑定的全部设备（主设备 + 各单元规程绑定的设备）。</summary>
    public static IReadOnlyList<Guid> BoundEquipmentIds(ProductionBatch batch) =>
        UnitEquipmentBinding.AllIds(Deserialize(batch.ControlRecipeJson), batch.EquipmentId);
}
