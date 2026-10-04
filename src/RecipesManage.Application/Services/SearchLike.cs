namespace RecipesManage.Application.Services;

/// <summary>
/// 列表搜索词转 LIKE 模式的唯一口径：空串视为不过滤；通配符要转义，
/// 否则用户输入 % 就等于"匹配所有"。
/// 批次、报警、审计三处的服务端搜索都从这里取，改动转义规则不会再各改一遍。
/// </summary>
internal static class SearchLike
{
    internal static string? Normalize(string? q) =>
        string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
}