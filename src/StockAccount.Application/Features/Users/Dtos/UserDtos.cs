using StockAccount.Application.Common.Paging;

namespace StockAccount.Application.Features.Users.Dtos;

public sealed record UserDto(
    int Id,
    string Email,
    string FullName,
    string Role,
    int? CompanyId,
    string? CompanyName,
    int? CurrentAccountId,
    string? CurrentAccountTitle,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public sealed class UserCreateRequest
{
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>"Admin" or "User".</summary>
    public string Role { get; set; } = string.Empty;
    public int? CompanyId { get; set; }
    public int? CurrentAccountId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class UserUpdateRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int? CompanyId { get; set; }
    public int? CurrentAccountId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>If set, resets the password.</summary>
    public string? NewPassword { get; set; }
}

/// <summary>GET /users query: q (email, fullName), role, companyId, isActive; sort email,fullName,createdAt,lastLoginAt.</summary>
public sealed class UserListQuery : PagedRequest
{
    public string? Role { get; set; }
    public int? CompanyId { get; set; }
    public bool? IsActive { get; set; }
}
