using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Api;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth, IConfiguration config) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        return Ok(new LoginResponse(JwtIssuer.Issue(config, user), AuthService.ToDto(user)));
    }

    [Authorize]
    [HttpGet("me")]
    public ActionResult<UserDto> Me([FromServices] ICurrentUser current)
    {
        if (!current.IsAuthenticated || current.UserId is null || current.Role is null)
            return Unauthorized();
        return Ok(new UserDto(current.UserId.Value, current.UserName, current.DisplayName, current.Role.Value));
    }
}
