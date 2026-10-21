using System.Linq.Expressions;
using StockAccount.Application.Common.Paging;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Stocks.Dtos;

public sealed record StockDto(
    int Id,
    int CompanyId,
    string CompanyName,
    string Code,
    string Name,
    StockUnit Unit,
    string? Barcode,
    decimal VatRate,
    decimal UnitPrice,
    string? Description,
    bool IsActive,
    decimal Balance,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string RowVersion);

/// <summary>Create/update body. Balance is system-maintained and never accepted. <see cref="RowVersion"/> required on PUT.</summary>
public sealed class StockSaveRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StockUnit Unit { get; set; }
    public string? Barcode { get; set; }
    public decimal VatRate { get; set; } = 20m;
    public decimal UnitPrice { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

/// <summary>GET /stocks query: q (code, name, barcode), unit, isActive; sort code,name,unit,unitPrice,balance,createdAt.</summary>
public sealed class StockListQuery : PagedRequest
{
    public StockUnit? Unit { get; set; }
    public bool? IsActive { get; set; }
}

public static class StockMappings
{
    /// <summary>EF projection (CompanyName via join).</summary>
    public static readonly Expression<Func<Stock, StockDto>> Projection = s => new StockDto(
        s.Id, s.CompanyId, s.Company!.Name, s.Code, s.Name, s.Unit, s.Barcode, s.VatRate, s.UnitPrice, s.Description,
        s.IsActive, s.Balance, s.CreatedAt, s.UpdatedAt, Convert.ToBase64String(s.RowVersion));
}
