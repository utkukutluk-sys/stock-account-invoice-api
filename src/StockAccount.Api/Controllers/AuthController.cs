using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using StockAccount.Api.Setup;
using Microsoft.Extensions.Options;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Auth;
using StockAccount.Application.Features.Auth.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Login, cookie-based refresh token rotation, logout, current user, password change (CONTRACT §4.1, §5.4).</summary>
[Route("api/auth")]
[Tags("Auth")]
public sealed class AuthController(IAuthService auth, IOptions<JwtOptions> jwtOptions) : ApiControllerBase
{
    /// <summary>Empty JSON body (<c>{}</c>) for refresh/logout.</summary>
    public sealed class EmptyRequest;

    /// <summary>Logs in. The refresh token is set as the httpOnly <c>sa_refresh</c> cookie, never returned in the body.</summary>
    /// <response code="200">Access token + user info; Set-Cookie: sa_refresh.</response>
    /// <response code="401">INVALID_CREDENTIALS, ACCOUNT_LOCKED or USER_INACTIVE.</response>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitingOptions.AuthPolicy)]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        SetRefreshCookie(result);
        return Ok(result.Response);
    }

    /// <summary>Rotates the refresh token from the <c>sa_refresh</c> cookie and returns a new access token.</summary>
    /// <remarks>Only POST with Content-Type application/json and body <c>{}</c> is accepted (CSRF protection together with SameSite=Strict).</remarks>
    /// <response code="401">INVALID_REFRESH_TOKEN (missing/invalid/expired/reused cookie; the cookie is deleted).</response>
    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitingOptions.AuthPolicy)]
    [AllowAnonymous]
    [Consumes("application/json")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> Refresh(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] EmptyRequest? body, CancellationToken cancellationToken)
    {
        EnsureJsonRequest();
        try
        {
            var result = await auth.RefreshAsync(ReadRefreshCookie(), HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            SetRefreshCookie(result);
            return Ok(result.Response);
        }
        catch (AuthenticationFailedException)
        {
            DeleteRefreshCookie();
            throw;
        }
    }

    /// <summary>Revokes the refresh token in the cookie (if any) and deletes the cookie. Idempotent.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] EmptyRequest? body, CancellationToken cancellationToken)
    {
        EnsureJsonRequest();
        await auth.LogoutAsync(ReadRefreshCookie(), cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    /// <summary>Current user info.</summary>
    [HttpGet("me")]
    [Authorize(Policy = Policies.Authenticated)]
    [ProducesResponseType<UserInfo>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserInfo>> Me(CancellationToken cancellationToken) =>
        Ok(await auth.GetCurrentUserAsync(cancellationToken));

    /// <summary>Changes the caller's password.</summary>
    [HttpPost("change-password")]
    [Authorize(Policy = Policies.Authenticated)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await auth.ChangePasswordAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>Refresh/logout accept only <c>Content-Type: application/json</c> (CSRF hardening, CONTRACT §4.1) → else 415.</summary>
    private void EnsureJsonRequest()
    {
        if (!Request.HasJsonContentType())
        {
            throw new BadHttpRequestException("Content-Type application/json gereklidir.", StatusCodes.Status415UnsupportedMediaType);
        }
    }

    private string? ReadRefreshCookie() => Request.Cookies[jwtOptions.Value.RefreshCookieName];

    private void SetRefreshCookie(AuthResult result) =>
        Response.Cookies.Append(jwtOptions.Value.RefreshCookieName, result.RefreshToken, CookieOptions(result.RefreshTokenExpiresAt));

    private void DeleteRefreshCookie() =>
        Response.Cookies.Delete(jwtOptions.Value.RefreshCookieName, CookieOptions(expires: null));

    /// <summary>HttpOnly, SameSite=Strict, Secure when HTTPS, Path=/ (CONTRACT §4.1 path deviation for Nuxt SSR).</summary>
    private CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = Request.IsHttps,
        Path = "/",
        IsEssential = true,
        Expires = expires is DateTime e ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)) : null,
    };
}
