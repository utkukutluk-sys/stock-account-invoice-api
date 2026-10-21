using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Auth;
using StockAccount.Application.Features.Users.Dtos;

namespace StockAccount.Application.Features.Users;

public sealed class UserService(
    IApplicationDbContext db,
    UserManager<AppUser> userManager,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<UserService> logger) : IUserService
{
    public static readonly SortMap<AppUser> Sorts = new SortMap<AppUser>("email", u => u.Id)
        .Add("email", u => u.Email)
        .Add("fullName", u => u.FullName)
        .Add("createdAt", u => u.CreatedAt)
        .Add("lastLoginAt", u => u.LastLoginAt);

    public Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken = default)
    {
        var q = db.Users.AsNoTracking();
        if (query.Search is { } s)
        {
            q = q.Where(u => (u.Email != null && u.Email.Contains(s)) || u.FullName.Contains(s));
        }

        if (!string.IsNullOrEmpty(query.Role))
        {
            var role = query.Role;
            q = q.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == role)));
        }

        if (query.CompanyId is int companyId)
        {
            q = q.Where(u => u.CompanyId == companyId);
        }

        if (query.IsActive is bool active)
        {
            q = q.Where(u => u.IsActive == active);
        }

        return Project(q.ApplySort(query.Sort, Sorts)).ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }

    public async Task<UserDto> GetAsync(int id, CancellationToken cancellationToken = default) =>
        await Project(db.Users.AsNoTracking().Where(u => u.Id == id)).FirstOrDefaultAsync(cancellationToken)
        ?? throw NotFoundException.For("Kullanıcı");

    public async Task<UserDto> CreateAsync(UserCreateRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            throw DuplicateEmail();
        }

        var (companyId, accountId) = await ValidateLinksAsync(request.Role, request.CompanyId, request.CurrentAccountId, null, cancellationToken);
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            CompanyId = companyId,
            CurrentAccountId = accountId,
            IsActive = request.IsActive,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        await InTransactionAsync(async () =>
        {
            ThrowIfFailed(await userManager.CreateAsync(user, request.Password), "password");
            ThrowIfFailed(await userManager.AddToRoleAsync(user, request.Role), "role");
        }, cancellationToken);

        logger.LogInformation("User {UserId} ({Email}) created with role {Role} by {AdminId}", user.Id, email, request.Role, currentUser.UserId);
        return await GetAsync(user.Id, cancellationToken);
    }

    public async Task<UserDto> UpdateAsync(int id, UserUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)) ?? throw NotFoundException.For("Kullanıcı");
        var (companyId, accountId) = await ValidateLinksAsync(request.Role, request.CompanyId, request.CurrentAccountId, id, cancellationToken);
        var deactivated = user.IsActive && !request.IsActive;

        await InTransactionAsync(async () =>
        {
            user.FullName = request.FullName.Trim();
            user.CompanyId = companyId;
            user.CurrentAccountId = accountId;
            user.IsActive = request.IsActive;
            ThrowIfFailed(await userManager.UpdateAsync(user), "fullName");

            var roles = await userManager.GetRolesAsync(user);
            if (!roles.SequenceEqual([request.Role]))
            {
                if (roles.Count > 0)
                {
                    ThrowIfFailed(await userManager.RemoveFromRolesAsync(user, roles), "role");
                }

                ThrowIfFailed(await userManager.AddToRoleAsync(user, request.Role), "role");
            }

            if (!string.IsNullOrEmpty(request.NewPassword))
            {
                if (await userManager.HasPasswordAsync(user))
                {
                    ThrowIfFailed(await userManager.RemovePasswordAsync(user), "newPassword");
                }

                ThrowIfFailed(await userManager.AddPasswordAsync(user, request.NewPassword), "newPassword");
            }

            if (deactivated)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, AuthService.RevokedDeactivated), cancellationToken);
            }
        }, cancellationToken);

        logger.LogInformation("User {UserId} updated by {AdminId} (active: {IsActive})", id, currentUser.UserId, request.IsActive);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id == currentUser.UserId)
        {
            throw new ConflictException(ErrorCodes.CannotDeleteSelf, "Kendi hesabınızı silemezsiniz.");
        }

        var user = await userManager.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)) ?? throw NotFoundException.For("Kullanıcı");
        ThrowIfFailed(await userManager.DeleteAsync(user), "id");
        logger.LogInformation("User {UserId} deleted by {AdminId}", id, currentUser.UserId);
    }

    private IQueryable<UserDto> Project(IQueryable<AppUser> q) => q.Select(u => new UserDto(
        u.Id,
        u.Email ?? string.Empty,
        u.FullName,
        db.UserRoles.Where(ur => ur.UserId == u.Id).Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).FirstOrDefault() ?? string.Empty,
        u.CompanyId,
        u.Company != null ? u.Company.Name : null,
        u.CurrentAccountId,
        u.CurrentAccount != null ? u.CurrentAccount.Title : null,
        u.IsActive,
        u.CreatedAt,
        u.LastLoginAt));

    /// <summary>Role rules: User → company required, optional portal account of that company not linked to anyone else.</summary>
    private async Task<(int? CompanyId, int? AccountId)> ValidateLinksAsync(
        string role, int? companyId, int? accountId, int? userId, CancellationToken cancellationToken)
    {
        if (role == Roles.Admin)
        {
            return (null, null);
        }

        if (companyId is not int cid || !await db.Companies.AnyAsync(c => c.Id == cid, cancellationToken))
        {
            throw new BusinessRuleException(ErrorCodes.InvalidReference, "Firma bulunamadı.");
        }

        if (accountId is int aid)
        {
            if (!await db.CurrentAccounts.AnyAsync(a => a.Id == aid && a.CompanyId == cid, cancellationToken))
            {
                throw new BusinessRuleException(ErrorCodes.InvalidReference, "Cari hesap bulunamadı veya seçili firmaya ait değil.");
            }

            if (await db.Users.AnyAsync(u => u.CurrentAccountId == aid && u.Id != userId, cancellationToken))
            {
                throw new ConflictException(ErrorCodes.AccountInUse, "Bu cari hesap başka bir kullanıcıya bağlı.");
            }
        }

        return (cid, accountId);
    }

    private async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async ct =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await work();
                await tx.CommitAsync(ct);
            },
            cancellationToken);
    }

    private static void ThrowIfFailed(IdentityResult result, string property)
    {
        if (result.Succeeded)
        {
            return;
        }

        if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            throw DuplicateEmail();
        }

        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, PersistenceExtensions.ConcurrencyMessage);
        }

        throw ValidationHelpers.Failure(property, string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    private static ConflictException DuplicateEmail() => new(ErrorCodes.DuplicateEmail, "Bu e-posta adresi ile kayıtlı bir kullanıcı zaten var.");
}
