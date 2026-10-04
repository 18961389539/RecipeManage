using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Api.Localization;
using RecipesManage.Domain.Common;

namespace RecipesManage.Api;

/// <summary>
/// 异常 → HTTP 语义的唯一映射点。
///
/// 为什么在意状态码：前端目前只把 message 弹出来，所以 400 和 409 看起来一样；
/// 但"并发覆盖了别人的写入""编号重复""设备被占着"是<strong>重试就可能成功</strong>的一类，
/// 与"参数不合法"（重试一万次也一样错）混在 400 里，客户端与监控面板就再也分不出这两类。
/// 设备占用用 423 Locked（RFC 4918），语义正是"资源当前被锁住"。
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> log)
{
    /// <summary>请求本身没错，是服务器当前状态与它冲突：刷新/重试是有意义的动作。</summary>
    private static readonly HashSet<string> ConflictCodes =
    [
        "CONFLICT", "DUP_BATCH", "DUP_CODE", "DUP_LOT", "DUP_SAMPLE", "DUP_USER",
        "ALREADY_DONE", "ALREADY_DECIDED", "DRAFT_EXISTS", "VERSION_MISMATCH",
        "EVIDENCE_STALE", "EVIDENCE_INVALID"
    ];

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            await WriteAsync(context, StatusFor(ex.Code), ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // ConcurrencyStamp 撞车：另一个写入（HTTP 或调度线程）已经改过这一行。
            log.LogInformation(ex, "并发冲突 {Path}", context.Request.Path);
            await WriteAsync(context, StatusCodes.Status409Conflict, "CONFLICT",
                "数据已被其他操作更新，请刷新后重试。");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // 先查后写只挡住常见路径，真正排他还是靠唯一索引；撞上了要能看出来是什么，
            // 而不是 500「服务器内部错误」——那会让"批次号重复"变成不可诊断的故障。
            log.LogWarning(ex, "唯一约束冲突 {Path}", context.Request.Path);
            await WriteAsync(context, StatusCodes.Status409Conflict, "CONFLICT",
                "该编号已存在或与其他记录冲突，请刷新后重试。");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开或页面轮询取消，不算服务器故障。
        }
        catch (Exception ex)
        {
            log.LogError(ex, "unhandled");
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "INTERNAL", "服务器内部错误。");
        }
    }

    private static int StatusFor(string code) => code switch
    {
        "AUTH" => StatusCodes.Status401Unauthorized,
        "FORBIDDEN" => StatusCodes.Status403Forbidden,
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "EQ_BUSY" => StatusCodes.Status423Locked,
        // 登录限流：429 让客户端能区分"密码错了"和"先等一会儿"。
        "TOO_MANY_ATTEMPTS" => StatusCodes.Status429TooManyRequests,
        _ => ConflictCodes.Contains(code) ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest
    };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message?.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;

    private static Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        // 只译这一层：message 是给界面弹出来看的瞬时提示。审计与电子签名原文走的是响应体里的
        // 数据字段，不经过这里，所以不会被追溯性地改写。
        return context.Response.WriteAsJsonAsync(new { code, message = MessageCatalog.Localize(message, RequestCulture(context)) });
    }

    /// <summary>
    /// 取 Accept-Language 的第一个标签。有意不接 .NET 的 `RequestLocalization`：
    /// 那会按服务器文化给数字/日期排序，把车间里统一的 `2026-09-19 23:30:00` 变成 locale 相关格式。
    /// </summary>
    private static CultureInfo? RequestCulture(HttpContext context)
    {
        var header = context.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return null;
        var first = header.Split(',')[0].Split(';')[0].Trim();
        try
        {
            return string.IsNullOrEmpty(first) ? null : new CultureInfo(first);
        }
        catch (CultureNotFoundException)
        {
            // 乱填的语言头不该让一个已经决定好的错误响应变成 500。
            return null;
        }
    }
}

[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase;
