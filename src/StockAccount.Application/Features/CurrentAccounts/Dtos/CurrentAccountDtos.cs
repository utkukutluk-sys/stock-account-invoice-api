using System.Linq.Expressions;
using StockAccount.Application.Common.Paging;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.CurrentAccounts.Dtos;

public sealed record CurrentAccountDto(
    int Id,
    int CompanyId,
    string CompanyName,
    string Code,
    string Title,
    CurrentAccountType Type,
    string? TaxNumber,
    string? TaxOffice,
    string? Phone,
    string? Email,
    string? Address,
    string? City,
    bool IsActive,
    decimal Balance,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string RowVersion);

/// <summary>Create/update body. Balance is system-maintained. <see cref="RowVersion"/> required on PUT.</summary>
public sealed class CurrentAccountSaveRequest
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public CurrentAccountType Type { get; set; }
    public string? TaxNumber { get; set; }
    public string? TaxOffice { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

/// <summary>GET /current-accounts query: q (code, title, taxNumber), type, city, isActive.</summary>
public sealed class CurrentAccountListQuery : PagedRequest
{
    public CurrentAccountType? Type { get; set; }
    public string? City { get; set; }
    public bool? IsActive { get; set; }
}

public static class CurrentAccountMappings
{
    public static readonly Expression<Func<CurrentAccount, CurrentAccountDto>> Projection = a => new CurrentAccountDto(
        a.Id, a.CompanyId, a.Company!.Name, a.Code, a.Title, a.Type, a.TaxNumber, a.TaxOffice, a.Phone, a.Email, a.Address, a.City,
        a.IsActive, a.Balance, a.CreatedAt, a.UpdatedAt, Convert.ToBase64String(a.RowVersion));
}
