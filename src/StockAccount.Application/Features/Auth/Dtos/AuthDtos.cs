namespace StockAccount.Application.Features.Auth.Dtos;

public sealed class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>Login/refresh response body. The refresh token is never in the body (httpOnly cookie, CONTRACT §4.1).</summary>
public sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresAt, UserInfo User);

public sealed record UserInfo(
    int Id,
    string Email,
    string FullName,
    string Role,
    int? CompanyId,
    string? CompanyName,
    int? CurrentAccountId,
    string? CurrentAccountTitle,
    bool IsPortalUser);

/// <summary>Service result: response body + the raw refresh token the Api puts into the cookie.</summary>
public sealed record AuthResult(AuthResponse Response, string RefreshToken, DateTime RefreshTokenExpiresAt);
