using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Auth.Dtos;

namespace StockAccount.Application.Features.Auth;

public sealed class AuthService(
    IApplicationDbContext db,
    UserManager<AppUser> userManager,
    ITokenService tokenService,
    ICurrentUser currentUser,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    ILogger<AuthService> logger) : IAuthService
{
    public const string RevokedRotated = "Rotated";
    public const string RevokedReuse = "Reuse detected";
    public const string RevokedLogout = "Logout";
    public const string RevokedDeactivated = "User deactivated";

    private JwtOptions Options => jwtOptions.Value;

    public async Task<AuthResult> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            logger.LogInformation("Login failed for {Email}: unknown user (IP {IpAddress})", email, ipAddress);
            throw InvalidCredentials();
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            logger.LogInformation("Login rejected for {Email}: account locked", email);
            throw AccountLocked();
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            if (await userManager.IsLockedOutAsync(user))
            {
                logger.LogWarning("Login failed for {Email}: wrong password, account is now locked", email);
                throw AccountLocked();
            }

            logger.LogInformation("Login failed for {Email}: wrong password (IP {IpAddress})", email, ipAddress);
            throw InvalidCredentials();
        }

        if (!user.IsActive)
        {
            logger.LogInformation("Login rejected for {Email}: user inactive", email);
            throw new AuthenticationFailedException(ErrorCodes.UserInactive, "Kullanıcı hesabı pasif durumda.");
        }

        await userManager.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = Now;
        var result = await IssueTokensAsync(user, ipAddress, cancellationToken);
        logger.LogInformation("Login succeeded for {Email} (user {UserId}, role {Role})", email, user.Id, result.Response.User.Role);
        return result;
    }

    public async Task<AuthResult> RefreshAsync(string? rawRefreshToken, string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw InvalidRefreshToken();
        }

        var now = Now;
        var hash = RefreshTokenGenerator.Hash(rawRefreshToken);
        var token = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token?.User is null)
        {
            throw InvalidRefreshToken();
        }

        if (token.RevokedAt is DateTime revokedAt)
        {
            var withinGrace = token.RevokedReason == RevokedRotated
                && (now - revokedAt).TotalSeconds < Options.RefreshReuseGraceSeconds;
            if (withinGrace)
            {
                // Parallel tab / SSR race: the other request won the rotation. Not treated as reuse.
                logger.LogInformation("Refresh token of user {UserId} presented again within the grace window", token.UserId);
                throw InvalidRefreshToken();
            }

            var revoked = await RevokeAllAsync(token.UserId, RevokedReuse, cancellationToken);
            logger.LogWarning("Refresh token reuse detected for user {UserId}; revoked {Count} active tokens", token.UserId, revoked);
            throw InvalidRefreshToken();
        }

        if (token.ExpiresAt <= now || !token.User.IsActive)
        {
            throw InvalidRefreshToken();
        }

        // Atomic rotation: only one concurrent request can revoke this row; the loser gets 401 (grace semantics).
        var newRaw = RefreshTokenGenerator.Generate();
        var newHash = RefreshTokenGenerator.Hash(newRaw);
        var affected = await db.RefreshTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.RevokedReason, RevokedRotated)
                    .SetProperty(t => t.ReplacedByTokenHash, newHash),
                cancellationToken);
        if (affected == 0)
        {
            throw InvalidRefreshToken();
        }

        return await IssueTokensAsync(token.User, ipAddress, cancellationToken, (newRaw, newHash));
    }

    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return;
        }

        var hash = RefreshTokenGenerator.Hash(rawRefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = Now;
            token.RevokedReason = RevokedLogout;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("User {UserId} logged out", token.UserId);
        }
    }

    public async Task<UserInfo> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationFailedException(ErrorCodes.Unauthorized, "Oturum bulunamadı.");
        return await UserInfoQuery.LoadAsync(db, userId, cancellationToken)
            ?? throw new AuthenticationFailedException(ErrorCodes.Unauthorized, "Kullanıcı bulunamadı.");
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationFailedException(ErrorCodes.Unauthorized, "Oturum bulunamadı.");
        var user = await userManager.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new AuthenticationFailedException(ErrorCodes.Unauthorized, "Kullanıcı bulunamadı.");

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
            {
                throw ValidationHelpers.Failure("currentPassword", "Mevcut şifre hatalı.");
            }

            throw ValidationHelpers.Failure("newPassword", string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("User {UserId} changed password", userId);
    }

    private async Task<AuthResult> IssueTokensAsync(
        AppUser user, string? ipAddress, CancellationToken cancellationToken, (string Raw, string Hash)? preGenerated = null)
    {
        var (raw, hash) = preGenerated ?? NewToken();
        var expires = Now.AddDays(Options.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hash,
            CreatedAt = Now,
            ExpiresAt = expires,
            CreatedByIp = ipAddress is { Length: > 45 } ? ipAddress[..45] : ipAddress,
        });
        await db.SaveChangesAsync(cancellationToken);

        var info = await UserInfoQuery.LoadAsync(db, user.Id, cancellationToken)
            ?? throw new AuthenticationFailedException(ErrorCodes.Unauthorized, "Kullanıcı bulunamadı.");
        var access = tokenService.CreateAccessToken(user, info.Role);
        return new AuthResult(new AuthResponse(access.Token, access.ExpiresAt, info), raw, expires);

        static (string, string) NewToken()
        {
            var r = RefreshTokenGenerator.Generate();
            return (r, RefreshTokenGenerator.Hash(r));
        }
    }

    private async Task<int> RevokeAllAsync(int userId, string reason, CancellationToken cancellationToken)
    {
        var now = Now;
        return await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private static AuthenticationFailedException InvalidCredentials() =>
        new(ErrorCodes.InvalidCredentials, "E-posta veya şifre hatalı.");

    private static AuthenticationFailedException AccountLocked() =>
        new(ErrorCodes.AccountLocked, "Çok sayıda hatalı giriş nedeniyle hesap geçici olarak kilitlendi. Lütfen 5 dakika sonra tekrar deneyin.");

    private static AuthenticationFailedException InvalidRefreshToken() =>
        new(ErrorCodes.InvalidRefreshToken, "Oturum süresi doldu. Lütfen tekrar giriş yapın.");
}
