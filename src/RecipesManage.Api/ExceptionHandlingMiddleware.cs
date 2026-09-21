using Microsoft.AspNetCore.Mvc;
using RecipesManage.Domain.Common;

namespace RecipesManage.Api;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> log)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            context.Response.StatusCode = ex.Code is "AUTH" ? StatusCodes.Status401Unauthorized
                : ex.Code is "FORBIDDEN" ? StatusCodes.Status403Forbidden
                : ex.Code is "NOT_FOUND" ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开或页面轮询取消，不算服务器故障。
        }
        catch (Exception ex)
        {
            log.LogError(ex, "unhandled");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { code = "INTERNAL", message = "服务器内部错误。" });
        }
    }
}

[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase;
