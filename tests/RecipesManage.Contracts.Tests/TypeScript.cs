using System.Reflection;
using System.Text.RegularExpressions;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;

namespace RecipesManage.Contracts.Tests;

/// <summary>
/// 够用的 <c>types.ts</c> 解析器：只取顶层 <c>export interface</c> 的成员名与成员类型文本，
/// 以及 <c>export type X = "a" | "b"</c> 具名联合。
///
/// 不做通用 TS 解析（那要引依赖），但会跳过注释与字符串里的括号，
/// 并且遇到本解析器处理不了的写法（如 <c>extends</c>）直接抛，宁可报错也不静默漏检。
/// </summary>
internal sealed class TypeScript
{
    private const string RelativePath = "frontend/src/api/types.ts";

    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Interfaces { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases { get; init; }

    /// <summary>两侧程序集里按简单名索引的公开枚举。</summary>
    public static readonly IReadOnlyDictionary<string, Type> EnumsByName =
        new[] { typeof(UserDto).Assembly, typeof(BatchStatus).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsPublic && t.IsEnum)
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public static TypeScript Read()
    {
        var source = StripComments(File.ReadAllText(Locate()));
        var interfaces = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(source, @"export\s+interface\s+(\w+)([^{]*)\{"))
        {
            if (match.Groups[2].Value.Contains("extends", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"types.ts 的 {match.Groups[1].Value} 用了 extends，契约解析器不支持，请展开字段或升级解析器。");
            interfaces[match.Groups[1].Value] = Members(BodyAt(source, match.Index + match.Length - 1));
        }

        foreach (Match match in Regex.Matches(source, @"export\s+type\s+(\w+)\s*=\s*([^;]+);"))
            aliases[match.Groups[1].Value] = Literals(match.Groups[2].Value) ?? [];

        return new TypeScript { Interfaces = interfaces, Aliases = aliases };
    }

    /// <summary>成员类型里的字符串联合；不是联合（或解析不出）时返回 null。</summary>
    public IReadOnlyList<string>? UnionOf(string declared)
    {
        var literals = Literals(declared);
        if (literals is not null)
            return literals;

        var bare = declared.Trim();
        if (bare.EndsWith("[]", StringComparison.Ordinal))
            bare = bare[..^2].Trim();
        bare = Regex.Replace(bare, @"^(?:null\s*\|\s*)|(?:\s*\|\s*null)$", "").Trim();
        return Aliases.TryGetValue(bare, out var alias) ? alias : null;
    }

    private static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 向上找不到 {RelativePath}，契约测试必须在仓库内运行。");
    }

    private static IReadOnlyDictionary<string, string> Members(string body)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in body.Split(';'))
        {
            var separator = TopLevelColon(declaration);
            if (separator < 0)
                continue; // 方法签名或索引签名，本文件没有需要比对的这类写法

            var name = declaration[..separator].Trim().TrimEnd('?').Trim();
            if (name.Length == 0 || name[0] == '[' || !Regex.IsMatch(name, @"^[A-Za-z_$][\w$]*$"))
                continue;
            members[name] = declaration[(separator + 1)..].Trim();
        }

        return members;
    }

    /// <summary>取跳过括号与尖括号后的第一个冒号，即"成员名 : 类型"的分界。</summary>
    private static int TopLevelColon(string declaration)
    {
        var depth = 0;
        for (var i = 0; i < declaration.Length; i++)
        {
            var c = declaration[i];
            if (c is '(' or '[' or '{' or '<')
                depth++;
            else if (c is ')' or ']' or '}' or '>')
                depth--;
            else if (c == ':' && depth == 0)
                return i;
        }

        return -1;
    }

    private static string BodyAt(string source, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[(openBrace + 1)..i];
            }
        }

        throw new InvalidOperationException("types.ts 有未闭合的 interface 块。");
    }

    private static IReadOnlyList<string>? Literals(string typeText) =>
        Regex.Matches(typeText, @"""([^""]*)""").Select(m => m.Groups[1].Value).ToList() is { Count: > 0 } found
            ? found
            : null;

    private static string StripComments(string source)
    {
        var builder = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                builder.Append('\n');
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 1;
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                builder.Append(c);
                for (i++; i < source.Length; i++)
                {
                    builder.Append(source[i]);
                    if (source[i] == '\\')
                    {
                        if (++i < source.Length)
                            builder.Append(source[i]);
                        continue;
                    }

                    if (source[i] == c)
                        break;
                }

                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
