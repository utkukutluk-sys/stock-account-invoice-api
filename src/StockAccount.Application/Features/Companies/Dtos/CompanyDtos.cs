using StockAccount.Application.Common.Paging;
using StockAccount.Domain.Entities;

namespace StockAccount.Application.Features.Companies.Dtos;

public sealed record CompanyDto(
    int Id,
    string Name,
    string TaxNumber,
    string? TaxOffice,
    string? Phone,
    string? Email,
    string? Address,
    string? City,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string RowVersion);

/// <summary>Create/update body. <see cref="RowVersion"/> is required on PUT.</summary>
public sealed class CompanySaveRequest
{
    public string Name { get; set; } = string.Empty;
    public string TaxNumber { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

/// <summary>GET /companies query: q (name, taxNumber, city), isActive; sort name,taxNumber,city,createdAt.</summary>
public sealed class CompanyListQuery : PagedRequest
{
    public bool? IsActive { get; set; }
}

public static class CompanyMappings
{
    public static CompanyDto ToDto(this Company c) => new(
        c.Id, c.Name, c.TaxNumber, c.TaxOffice, c.Phone, c.Email, c.Address, c.City, c.IsActive, c.CreatedAt, c.UpdatedAt,
        Convert.ToBase64String(c.RowVersion));
}
