using Microsoft.AspNetCore.Identity;

namespace StockAccount.Application.Common.Identity;

/// <summary>Identity role (table AspNetRoles). Exactly two roles exist: <see cref="Roles.Admin"/> and <see cref="Roles.User"/>.</summary>
public class AppRole : IdentityRole<int>
{
    public AppRole()
    {
    }

    public AppRole(string roleName)
        : base(roleName)
    {
    }
}

/// <summary>Role name constants (CONTRACT §4).</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Admin, User];
}
