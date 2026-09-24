using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// <see cref="RecipesManage.Domain.Identity.AuditLog.At"/> 的存储映射：定宽前缀 + 变长小数秒的 TEXT。
///
/// 为什么要显式写转换器：SQLite 提供器**禁止** <c>ORDER BY</c> 原生 DateTimeOffset 列
/// （它无法保证跨偏移的字典序即时间序），于是审计履历的排序与分页只能把整张表读进内存做，
/// 履历越长越贵。这套写法把可变的部分固定下来：
/// <list type="bullet">
/// <item>与 EF 自带映射逐字符一致（<c>yyyy-MM-dd HH:mm:ss.FFFFFFFzzz</c>），所以历史行不需要回填；</item>
/// <item>日期时间部分定宽，小数秒变短也不会错序——终止符 <c>+</c>(0x2B) 比任何数字都小，
/// 前缀较短的分数天然排在更长的分数之前（<c>:53.2</c> 早于 <c>:53.25</c>）；</item>
/// <item>写入前统一 <c>ToUniversalTime()</c>，全库偏移恒为 <c>+00:00</c>，字典序才等于时间序。</item>
/// </list>
/// 代价是这条映射依赖"偏移始终为 0"这个不变量：真要存本地时区的时间，得换成 ticks 列。
/// </summary>
internal static class AuditTimestamp
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";

    public static readonly ValueConverter<DateTimeOffset, string> Converter = new(
        value => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
        text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
