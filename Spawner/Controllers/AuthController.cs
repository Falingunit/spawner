using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Spawner.Services;

namespace Spawner.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AdminAuthentication auth, IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("session")]
    public IActionResult Session() => Ok(new
    {
        authenticated = User.Identity?.IsAuthenticated == true,
        username = User.Identity?.Name,
        csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
    });

    public sealed record LoginRequest([Required, MaxLength(128)] string Username, [Required, MaxLength(1024)] string Password);

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [RequestSizeLimit(8192)]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        if (!auth.Verify(request.Username, request.Password))
            return Unauthorized(new { error = new { message = "Invalid username or password." } });
        auth.Revoke(User.FindFirstValue(ClaimTypes.NameIdentifier));
        await HttpContext.SignInAsync(AdminAuthentication.Scheme, auth.CreateSession(), new AuthenticationProperties { IsPersistent = false });
        return NoContent();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        auth.Revoke(User.FindFirstValue(ClaimTypes.NameIdentifier));
        await HttpContext.SignOutAsync(AdminAuthentication.Scheme);
        return NoContent();
    }
}
