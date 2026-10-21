using System.Linq.Expressions;
using StockAccount.Application.Common.Paging;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Invoices.Dtos;

public sealed record InvoiceLineDto(
    int Id,
    int LineNo,
    int StockId,
    string StockCode,
    string StockName,
    StockUnit Unit,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal VatRate,
    decimal LineNet,
    decimal VatAmount,
    decimal LineTotal);

public sealed record InvoiceDto(
    int Id,
    int CompanyId,
    string CompanyName,
    InvoiceType Type,
    InvoiceStatus Status,
    string Series,
    int? Number,
    string? DocumentNo,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    int CurrentAccountId,
    string CurrentAccountCode,
    string CurrentAccountTitle,
    string? Description,
    decimal SubTotal,
    decimal VatTotal,
    decimal GrandTotal,
    DateTime? ApprovedAt,
    DateTime? CancelledAt,
    string? CancelReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string RowVersion,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record InvoiceListItemDto(
    int Id,
    int CompanyId,
    string CompanyName,
    InvoiceType Type,
    InvoiceStatus Status,
    string Series,
    string? DocumentNo,
    DateOnly InvoiceDate,
    int CurrentAccountId,
    string CurrentAccountCode,
    string CurrentAccountTitle,
    decimal SubTotal,
    decimal VatTotal,
    decimal GrandTotal,
    int LineCount,
    DateTime CreatedAt);

/// <summary>Create/update body. Totals are always computed by the server. <see cref="Type"/> is ignored on PUT.</summary>
public sealed class InvoiceSaveRequest
{
    public InvoiceType? Type { get; set; }
    public string? Series { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public int CurrentAccountId { get; set; }
    public string? Description { get; set; }
    public List<InvoiceLineRequest> Lines { get; set; } = [];
    public string? RowVersion { get; set; }
}

public sealed class InvoiceLineRequest
{
    public int StockId { get; set; }
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
}

public sealed class InvoiceApproveRequest
{
    public string? RowVersion { get; set; }
}

public sealed class InvoiceCancelRequest
{
    public string? RowVersion { get; set; }
    public string? Reason { get; set; }
}

/// <summary>GET /invoices query: q (documentNo, account title/code, description), type, status, currentAccountId, series, dateFrom, dateTo.</summary>
public sealed class InvoiceListQuery : PagedRequest
{
    public InvoiceType? Type { get; set; }
    public InvoiceStatus? Status { get; set; }
    public int? CurrentAccountId { get; set; }
    public string? Series { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
}

/// <summary>GET /me/invoices query (portal): status (only Approved/Cancelled are ever returned).</summary>
public sealed class PortalInvoiceListQuery : PagedRequest
{
    public InvoiceStatus? Status { get; set; }
}

public static class InvoiceMappings
{
    public static readonly Expression<Func<Invoice, InvoiceListItemDto>> ListProjection = i => new InvoiceListItemDto(
        i.Id, i.CompanyId, i.Company!.Name, i.Type, i.Status, i.Series, i.DocumentNo, i.InvoiceDate, i.CurrentAccountId,
        i.CurrentAccount!.Code, i.CurrentAccount.Title, i.SubTotal, i.VatTotal, i.GrandTotal, i.Lines.Count, i.CreatedAt);

    public static readonly Expression<Func<Invoice, InvoiceDto>> DetailProjection = i => new InvoiceDto(
        i.Id, i.CompanyId, i.Company!.Name, i.Type, i.Status, i.Series, i.Number, i.DocumentNo, i.InvoiceDate, i.DueDate,
        i.CurrentAccountId, i.CurrentAccount!.Code, i.CurrentAccount.Title, i.Description, i.SubTotal, i.VatTotal, i.GrandTotal,
        i.ApprovedAt, i.CancelledAt, i.CancelReason, i.CreatedAt, i.UpdatedAt, Convert.ToBase64String(i.RowVersion),
        i.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineDto(
            l.Id, l.LineNo, l.StockId, l.Stock!.Code, l.Stock.Name, l.Stock.Unit, l.Description, l.Quantity, l.UnitPrice, l.VatRate,
            l.LineNet, l.VatAmount, l.LineTotal)).ToList());
}
