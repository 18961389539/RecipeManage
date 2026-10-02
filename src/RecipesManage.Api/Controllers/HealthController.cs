using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/health")]
public sealed class HealthController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var provider = RecipesDatabase.HealthName(db.Database);
        var ok = await db.Database.CanConnectAsync(ct);
        // 水位只在连得上的时候查：连不上时那两个字段本来就没有意义。
        var schema = ok ? await RecipesDatabase.SafeMigrationWatermarkAsync(db, ct) : null;
        return ok
            ? Ok(RecipesDatabase.HealthBody(provider, true, schema))
            : StatusCode(StatusCodes.Status503ServiceUnavailable, RecipesDatabase.HealthBody(provider, false));
    }
}
