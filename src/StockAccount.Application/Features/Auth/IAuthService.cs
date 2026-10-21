using StockAccount.Application.Features.Auth.Dtos;

namespace StockAccount.Application.Features.Auth;

public interface IAuthService
{
    /// <summary>401 INVALID_CREDENTIALS / ACCOUNT_LOCKED / USER_INACTIVE on failure.</summary>
    Task<AuthResult> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Rotates the refresh token (reuse detection + grace window). 401 INVALID_REFRESH_TOKEN on failure.</summary>
    Task<AuthResult> RefreshAsync(string? rawRefreshToken, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Revokes the token if it is active. Idempotent.</summary>
    Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken = default);

    Task<UserInfo> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
