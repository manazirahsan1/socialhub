using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Infrastructure;
using SocialHub.Api.Services;
using SocialHub.Shared;

namespace SocialHub.Api.Controllers;

[ApiController]
[Authorize]
public abstract class ApiControllerBase : ControllerBase
{
    protected string CurrentUserId => User.GetUserId();
}

[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting("auth")]
public class AuthController(UserManager<AppUser> users, TokenService tokens) : ApiControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req, CancellationToken ct)
    {
        var user = new AppUser
        {
            UserName = req.Email.Trim(),
            Email = req.Email.Trim(),
            DisplayName = req.DisplayName.Trim()
        };

        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        return Ok(await tokens.IssueAsync(user, ct));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null)
            return Problem(statusCode: 401, title: "Invalid email or password.");

        if (await users.IsLockedOutAsync(user))
            return Problem(statusCode: 423, title: "Account locked. Try again in a few minutes.");

        if (!await users.CheckPasswordAsync(user, req.Password))
        {
            await users.AccessFailedAsync(user); // counts toward lockout
            return Problem(statusCode: 401, title: "Invalid email or password.");
        }

        await users.ResetAccessFailedCountAsync(user);
        return Ok(await tokens.IssueAsync(user, ct));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest req, CancellationToken ct)
    {
        var response = await tokens.RefreshAsync(req.RefreshToken, ct);
        return response is null
            ? Problem(statusCode: 401, title: "Session expired. Please sign in again.")
            : Ok(response);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest req, CancellationToken ct)
    {
        await tokens.RevokeAsync(req.RefreshToken, ct);
        return NoContent();
    }
}
