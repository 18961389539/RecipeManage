using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using Xunit;

namespace RecipesManage.Contracts.Tests;

/// <summary>
/// 前端 <c>frontend/src/api/types.ts</c> 与后端 Dtos.cs 的契约守护。
///
/// 本仓库没有代码生成，接口改了字段名 / 加了枚举值，两侧都不会报错：
/// 实测 <c>DashboardDto.openAlarms</c> 在前端被写成可选，字段一旦改名，总览页只会安静地显示
/// "0 条待办"，而枚举加值会让标签、颜色和排序表安静地漏掉新状态。这里把两侧对起来，漂了就红。
///
/// 只检查"前端声明的字段后端必须真的返回"这个方向：后端多返回一个前端不用的字段不是缺陷。
/// 枚举则要求双向相等——前端必须覆盖全部状态，否则新状态在界面上没有中文标签。
/// </summary>
public sealed class ApiContractTests
{
    private static readonly TypeScript Frontend = TypeScript.Read();

    private static readonly Type[] DtoTypes = typeof(UserDto).Assembly.GetTypes()
        .Where(t => t.IsPublic && t.IsClass && t.Namespace == "RecipesManage.Application.Dtos")
        .ToArray();

    [Fact]
    public void Frontend_declares_only_fields_the_api_actually_returns()
    {
        var problems = new List<string>();
        foreach (var (interfaceName, members) in Frontend.Interfaces)
        {
            var dto = DtoTypes.FirstOrDefault(t => t.Name == interfaceName);
            if (dto is null)
                continue; // 前端独有的编辑态类型（SnapshotEdge 等）不参与比对

            var emitted = EmittedNames(dto);
            foreach (var member in members.Keys.Where(member => !emitted.Contains(member)))
                problems.Add($"{interfaceName}.{member}：types.ts 声明了，但 {dto.Name} 不返回该字段");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Enum_typed_fields_use_the_full_string_union()
    {
        var problems = new List<string>();
        foreach (var (interfaceName, members) in Frontend.Interfaces)
        {
            var dto = DtoTypes.FirstOrDefault(t => t.Name == interfaceName);
            if (dto is null)
                continue;

            foreach (var (name, enumType) in EnumProperties(dto))
            {
                if (!members.TryGetValue(name, out var declared))
                    continue; // 前端没用到这个枚举字段：由别名检查兜住，这里不催

                var union = Frontend.UnionOf(declared);
                if (union is null)
                {
                    problems.Add($"{interfaceName}.{name}：后端是枚举 {enumType.Name}，前端却写成 {declared.Trim()}");
                    continue;
                }

                Diff(problems, $"{interfaceName}.{name} ↔ {enumType.Name}", union, Enum.GetNames(enumType));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// types.ts 顶部那些具名联合必须与同名域枚举逐值相等。
    /// 前端可能整块不引用某个字段，但标签表 / 颜色表 / 排序表是按值列的，加一个状态就得同时改。
    /// </summary>
    [Fact]
    public void Named_frontend_unions_match_the_domain_enums()
    {
        var problems = new List<string>();
        foreach (var (alias, union) in Frontend.Aliases)
        {
            var enumType = TypeScript.EnumsByName.GetValueOrDefault(alias);
            if (enumType is null)
                continue; // 纯前端概念（如 HandshakeGroup）不比
            Diff(problems, $"{alias} ↔ {enumType.Name}", union, Enum.GetNames(enumType));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Diff(List<string> problems, string label, IEnumerable<string> frontend, IEnumerable<string> backend)
    {
        var left = frontend.ToList();
        var right = backend.ToList();
        var missing = right.Except(left, StringComparer.Ordinal).ToList();
        var extra = left.Except(right, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0)
            return;
        problems.Add($"{label}：前端缺 {string.Join('/', missing)}；前端多出 {string.Join('/', extra)}");
    }

    private static HashSet<string> EmittedNames(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<(string Name, Type EnumType)> EnumProperties(Type dto)
    {
        foreach (var property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (enumType.IsEnum)
                yield return (JsonNamingPolicy.CamelCase.ConvertName(property.Name), enumType);
        }
    }
}
