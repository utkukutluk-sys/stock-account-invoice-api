using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Features.Auth.Dtos;

namespace StockAccount.Application.Features.Auth;

/// <summary>Builds <see cref="UserInfo"/> (role, company and account names) for a user id.</summary>
public static class UserInfoQuery
{
    public static Task<UserInfo?> LoadAsync(IApplicationDbContext db, int userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .Where(u => u.Id == userId)
            .Select(u => new UserInfo(
                u.Id,
                u.Email ?? string.Empty,
                u.FullName,
                db.UserRoles.Where(ur => ur.UserId == u.Id).Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).FirstOrDefault() ?? Roles.User,
                u.CompanyId,
                u.Company != null ? u.Company.Name : null,
                u.CurrentAccountId,
                u.CurrentAccount != null ? u.CurrentAccount.Title : null,
                u.CurrentAccountId != null))
            .FirstOrDefaultAsync(cancellationToken);
}
