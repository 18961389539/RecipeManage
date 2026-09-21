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
        return ok
            ? Ok(RecipesDatabase.HealthBody(provider, true))
            : StatusCode(StatusCodes.Status503ServiceUnavailable, RecipesDatabase.HealthBody(provider, false));
    }
}
