using System.Text.RegularExpressions;
using Xunit;

namespace RecipesManage.Contracts.Tests;

/// <summary>
/// 后端 <see cref="MessageCatalog"/> 与源码里中文提示语的一致性。
///
/// 为什么按源码文本比而不是加一个 Api 项目引用来反射：这张表**故意**用中文原文当键
/// （和前端 types.ts 那套 source-as-key 一样），所以它的失效模式是"有人改了措辞，条目悄悄变成死键、
/// 界面退回中文"——反射看不出来，只有拿源码文本对才看得出来。本项目已经在用同一手法守 types.ts。
/// </summary>
public sealed class BackendMessageCatalogTests
{
    private const string CatalogRelative = "src/RecipesManage.Api/Localization/MessageCatalog.cs";

    /// <summary>
    /// 还没登记的静态中文提示数量上限。只许降不许升：加了新 DomainException 而没登记，
    /// 这条会红并告诉你当前真实数字（带变量的消息不算，见目录里的说明）。
    /// </summary>
    private const int UncoveredCeiling = 0;

    private static readonly string Root = Locate();
    private static readonly string CatalogSource = File.ReadAllText(Path.Combine(Root, CatalogRelative));

    /// <summary>源码里所有中文字面量（用于判死键），不含目录自身。</summary>
    private static readonly Lazy<string> AllSources = new(() =>
        string.Join("\n", Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("MessageCatalog.cs", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)));

    private static readonly Dictionary<string, string> Entries = Parse();

    private static Dictionary<string, string> Parse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(CatalogSource, @"\[\s*""((?:[^""]|"""")+)""\s*\]\s*=\s*""((?:[^""]|"""")+)"""))
            map.Add(Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value));
        return map;
    }

    private static string Unescape(string literal) => literal.Replace("\"\"", "\"");

    [Fact]
    public void Catalog_is_not_empty()
    {
        Assert.True(Entries.Count > 90, $"只解析到 {Entries.Count} 条，正则或目录写法可能变了");
    }

    [Fact]
    public void Every_key_is_still_a_message_the_source_can_produce()
    {
        // 措辞一改，条目就成死键、界面安静退回中文，编译和运行都不会报——只能这样钉住。
        var dead = Entries.Keys.Where(k => !AllSources.Value.Contains(k, StringComparison.Ordinal)).ToList();
        Assert.True(dead.Count == 0, "这些键在源码里已经找不到原文了（要么删掉，要么跟着措辞更新）：" +
                                      Environment.NewLine + string.Join(Environment.NewLine, dead));
    }

    [Fact]
    public void Every_entry_is_a_real_translation()
    {
        var blank = Entries.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ToList();
        Assert.True(blank.Count == 0, $"这些条目没有译文：{string.Join(" / ", blank)}");

        // 译文里还留着中文 = 复制粘贴时忘了改。
        var stillChinese = Entries.Where(e => Regex.IsMatch(e.Value, @"[一-鿿]")).Select(e => e.Key).ToList();
        Assert.True(stillChinese.Count == 0, $"这些译文含中文：{string.Join(" / ", stillChinese)}");
    }

    [Fact]
    public void Every_static_chinese_message_is_covered()
    {
        var uncovered = StaticMessages().Where(m => !Entries.ContainsKey(m)).ToList();
        Assert.True(uncovered.Count <= UncoveredCeiling,
            $"有 {uncovered.Count} 条静态中文提示没登记译文（当前数字改到 UncoveredCeiling，或补上译文）：" +
            Environment.NewLine + string.Join(Environment.NewLine, uncovered));
    }

    /// <summary>
    /// 走到异常处理中间件那两类调用点：DomainException 的纯字面量消息，
    /// 以及中间件自己写出的固定文案。带插值/拼接的一律排除——它们要的是消息模板，不是查表。
    /// </summary>
    private static IEnumerable<string> StaticMessages()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(AllSources.Value,
                     @"(?:DomainException|WriteAsync)\s*\([^;]*?\$\?""((?:[^""\\]|\\.)*)""\s*[,)]"))
        {
            var text = m.Groups[1].Value;
            if (text.Any(c => c >= '一' && c <= '鿿') && !text.Contains('{') && !text.Contains("$"))
                found.Add(text);
        }
        return found;
    }

    private static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, CatalogRelative.Replace('/', Path.DirectorySeparatorChar))))
                return dir.FullName;
        }
        throw new FileNotFoundException($"从 {AppContext.BaseDirectory} 向上找不到 {CatalogRelative}。");
    }
}
