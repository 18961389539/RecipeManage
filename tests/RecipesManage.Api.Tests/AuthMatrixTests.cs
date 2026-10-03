using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using RecipesManage.Api.Controllers;
using RecipesManage.Domain.Identity;
using Xunit;

namespace RecipesManage.Api.Tests;

/// <summary>
/// 授权矩阵：谁能调哪个接口。矩阵从代码里读（控制器上的特性 + <see cref="Capabilities"/>），不手写——
/// 手写的矩阵第一次改角色名单时就会过期，而一份过期的授权文档比没有更糟。
///
/// 三件事：
/// 1. <c>docs/auth-matrix.md</c> 里标记之间的内容必须与代码一致（不一致就失败，并说明怎么重新生成）；
/// 2. 每个接口都必须有明确的门槛——没有 <c>[Authorize]</c> / <c>[AllowAnonymous]</c> 的接口在 ASP.NET 里是对所有人开放的，
///    以后新增控制器漏标就会被这里拦下；
/// 3. 匿名接口与"只要登录就能写"的接口是两张封闭的白名单：新增一个就得在这里显式登记并写明理由。
/// </summary>
public sealed class AuthMatrixTests
{
    private const string Begin = "<!-- BEGIN:generated (由 AuthMatrixTests 生成，勿手改) -->";
    private const string End = "<!-- END:generated -->";
    private const string UpdateVariable = "UPDATE_AUTH_MATRIX";

    /// <summary>可以不登录就调用的接口。</summary>
    private static readonly string[] AnonymousAllowlist =
    [
        "POST api/auth/login",   // 登录本身
        "GET api/health",        // 探活（不含业务数据；根路径 /health 的最小 API 版在 Program.cs，不在控制器里，见文档）
    ];

    /// <summary>登录即可、但会改数据的接口：必须说明为什么静态能力表表达不了。</summary>
    private static readonly Dictionary<string, string> SignedInWrites = new()
    {
        ["POST api/recipes/{id:guid}/decide"] =
            "合法角色取决于配方当前停在哪个审核节点（运行时才定），由 RecipeApprovalService 按节点 RequiredRole 判定，并检查同一人不得担任多个节点",
    };

    [Fact]
    public void EveryEndpoint_DeclaresItsGate()
    {
        var open = Endpoints().Where(e => e.Gate.Kind == GateKind.Unspecified).Select(e => e.Key).ToList();
        Assert.True(open.Count == 0,
            "这些接口既没有 [Authorize] 也没有 [AllowAnonymous]，ASP.NET 会放行所有人：\n" + string.Join("\n", open));
    }

    [Fact]
    public void AnonymousEndpoints_AreExactlyTheAllowlist()
    {
        var actual = Endpoints().Where(e => e.Gate.Kind == GateKind.Anonymous).Select(e => e.Key).Order().ToList();
        Assert.Equal(AnonymousAllowlist.Order().ToList(), actual);
    }

    [Fact]
    public void SignedInOnlyWrites_AreExactlyTheJustifiedList()
    {
        var actual = Endpoints()
            .Where(e => e.Gate.Kind == GateKind.SignedIn && e.Verb != "GET")
            .Select(e => e.Key).Order().ToList();
        Assert.Equal(SignedInWrites.Keys.Order().ToList(), actual);
    }

    [Fact]
    public void EveryPolicyUsedOnAnEndpoint_IsARegisteredCapability()
    {
        var known = Capabilities.All.Select(c => c.Key).ToHashSet();
        var unknown = Endpoints().SelectMany(e => e.Gate.Policies).Where(p => !known.Contains(p)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "未登记的策略（运行时会抛 InvalidOperationException）：" + string.Join(", ", unknown));
    }

    [Fact]
    public void TheDocument_MatchesTheCode()
    {
        var path = DocumentPath();
        var current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var generated = Render();
        var expected = Replace(current, generated);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, expected, new UTF8Encoding(false));
            return;
        }

        Assert.True(expected is not null && expected == current,
            $"docs/auth-matrix.md 与代码不一致。重新生成：设环境变量 {UpdateVariable}=1 再跑本测试，然后检查 git diff 是否是你想要的授权变更。");
    }

    // ---------------------------------------------------------------- 生成

    private static string Render()
    {
        var roles = Enum.GetValues<UserRole>();
        var sb = new StringBuilder();

        sb.AppendLine("### 能力 × 角色");
        sb.AppendLine();
        sb.AppendLine("| 能力 | " + string.Join(" | ", roles.Select(RoleName)) + " |");
        sb.AppendLine("| --- | " + string.Join(" | ", roles.Select(_ => ":-:")) + " |");
        foreach (var capability in Capabilities.All)
            sb.AppendLine($"| `{capability.Key}` | " + string.Join(" | ", roles.Select(r => capability.Allows(r) ? "✓" : "")) + " |");

        sb.AppendLine();
        sb.AppendLine("### 接口 × 门槛");
        sb.AppendLine();
        sb.AppendLine("| 方法 | 路径 | 门槛 | 允许的角色 |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var e in Endpoints().OrderBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Verb, StringComparer.Ordinal))
            sb.AppendLine($"| {e.Verb} | `/{e.Route}` | {e.Gate.Describe()} | {e.Gate.RolesText(roles)} |");

        return sb.ToString().TrimEnd();
    }

    private static string? Replace(string document, string generated)
    {
        var start = document.IndexOf(Begin, StringComparison.Ordinal);
        var end = document.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end < start)
            return null;
        return document[..(start + Begin.Length)] + "\n\n" + generated + "\n\n" + document[end..];
    }

    private static string RoleName(UserRole role) => role switch
    {
        UserRole.Admin => "管理员",
        UserRole.ProcessEngineer => "工艺工程师",
        UserRole.Supervisor => "主管",
        UserRole.Quality => "质量",
        UserRole.Operator => "操作员",
        _ => role.ToString(),
    };

    private static string DocumentPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RecipesManage.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs", "auth-matrix.md");
    }

    // ---------------------------------------------------------------- 反射

    private enum GateKind { Unspecified, Anonymous, SignedIn, Policy }

    private sealed record Gate(GateKind Kind, IReadOnlyList<string> Policies)
    {
        public string Describe() => Kind switch
        {
            GateKind.Anonymous => "匿名",
            GateKind.SignedIn => "登录即可",
            GateKind.Policy => string.Join(" 且 ", Policies.Select(p => $"`{p}`")),
            _ => "**未声明**",
        };

        /// <summary>多个策略同时挂（控制器 + 动作）时是"且"：取各名单的交集。</summary>
        public string RolesText(IEnumerable<UserRole> all) => Kind switch
        {
            GateKind.Anonymous => "任何人",
            GateKind.SignedIn => "任何已登录用户",
            GateKind.Policy => string.Join("、", all
                .Where(r => Policies.All(p => Capabilities.All.Single(c => c.Key == p).Allows(r)))
                .Select(RoleName)),
            _ => "—",
        };
    }

    private sealed record Endpoint(string Verb, string Route, Gate Gate)
    {
        public string Key => $"{Verb} {Route}";
    }

    private static List<Endpoint> Endpoints()
    {
        var result = new List<Endpoint>();
        var controllers = typeof(AuthController).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t));

        foreach (var controller in controllers)
        {
            var baseRoute = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            baseRoute = baseRoute.Replace("[controller]", controller.Name.Replace("Controller", string.Empty).ToLowerInvariant());

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var http in action.GetCustomAttributes().OfType<HttpMethodAttribute>())
                {
                    var template = http.Template ?? string.Empty;
                    var route = string.Join('/', new[] { baseRoute, template }.Where(s => s.Length > 0));
                    foreach (var verb in http.HttpMethods)
                        result.Add(new Endpoint(verb, route, GateOf(controller, action)));
                }
            }
        }

        return result;
    }

    private static Gate GateOf(Type controller, MethodInfo action)
    {
        // ASP.NET：元数据里只要出现 AllowAnonymous（控制器或动作任一层），授权就被整体跳过。
        if (action.GetCustomAttribute<AllowAnonymousAttribute>() is not null ||
            controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            return new Gate(GateKind.Anonymous, []);

        var attributes = controller.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>())
            .ToList();

        if (attributes.Count == 0)
            return new Gate(GateKind.Unspecified, []);

        var policies = attributes.Select(a => a.Policy).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().Distinct().ToList();
        return policies.Count == 0
            ? new Gate(GateKind.SignedIn, [])
            : new Gate(GateKind.Policy, policies);
    }
}
