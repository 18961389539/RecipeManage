using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 控制配方快照的结构版本。
///
/// 为什么需要：快照封存之后永远不改（它是批记录的一部分，也是完整性哈希的对象），所以库里会同时躺着
/// 各个时期生成的快照。结构一变（加字段、改单位、换序列化口径），没有版本号就没法区分"旧批次"和"新批次"，
/// 只能靠"哈希试几种口径、看哪个对得上"——<see cref="SnapshotIntegrity"/> 已经被迫这么做过一次（v1 / v2 两套口径）。
///
/// 版本表：
/// <list type="bullet">
///   <item><b>0（<see cref="Legacy"/>）</b>：版本号出现之前的快照，JSON 里没有 schemaVersion。
///   哈希口径是历史的 V1（只含工步 / 连线）或 V2（再加缩放、批号、单元设备），读时两个都试。</item>
///   <item><b>1</b>：与 0 的字段形状相同，但 schemaVersion 本身纳入哈希（V3），
///   所以没法把一个新快照的版本号改回 0、让它按旧口径被解释而不被发现。</item>
/// </list>
///
/// 怎么改结构（以后的人照这个做）：
/// 1. <see cref="Current"/> 加一，在上面的版本表里写下新旧差别；
/// 2. 新版本的哈希口径写成新方法，<see cref="SnapshotIntegrity.Verify"/> 按快照自带的版本选口径，旧口径一行不改；
/// 3. 需要把旧快照读成新形状的，在读取处按 <see cref="ControlRecipeSnapshot.SchemaVersion"/> 解释，不改库里的 JSON；
/// 4. <c>SnapshotSchemaTests</c> 里钉着每个版本的"封存好的 JSON"，新代码必须仍把它们判为有效——改序列化选项或哈希口径而不升版本，
///    那几条就会变红。
/// </summary>
public static class SnapshotSchema
{
    /// <summary>版本号出现之前的快照（JSON 里没有该字段，反序列化得到 0）。</summary>
    public const int Legacy = 0;

    /// <summary>本程序生成快照时使用、并且能读懂的最高版本。</summary>
    public const int Current = 1;

    public static bool IsSupported(int version) => version is >= Legacy and <= Current;

    /// <summary>
    /// 快照由比本程序更新的版本生成时拒绝解释：按旧形状去读新快照，字段含义对不上，却不会报错，
    /// 最坏的结果是把参数按错的单位写给 PLC。典型场景是升级后又回退了程序。
    /// </summary>
    public static void DemandSupported(ControlRecipeSnapshot snapshot)
    {
        if (IsSupported(snapshot.SchemaVersion))
            return;
        throw new DomainException("SNAPSHOT_SCHEMA", Describe(snapshot.SchemaVersion));
    }

    public static string Describe(int version) =>
        version > Current
            ? $"控制配方快照由更新的程序版本生成（快照结构 v{version}，本程序只认到 v{Current}）。请升级程序，不要在旧版本上处理这个批次。"
            : $"控制配方快照的结构版本无效（v{version}）。";
}
