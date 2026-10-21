using System.Linq.Expressions;
using StockAccount.Application.Common.Paging;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Transactions.Dtos;

public sealed record StockTransDto(
    long Id,
    int CompanyId,
    int StockId,
    string StockCode,
    string StockName,
    StockUnit Unit,
    int InvoiceId,
    string? DocumentNo,
    DateOnly TransDate,
    StockTransDirection Direction,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    bool IsReversal,
    string Description,
    DateTime CreatedAt);

public sealed record ActTransDto(
    long Id,
    int CompanyId,
    int CurrentAccountId,
    string CurrentAccountCode,
    string CurrentAccountTitle,
    int InvoiceId,
    string? DocumentNo,
    DateOnly TransDate,
    ActTransDirection Direction,
    decimal Amount,
    bool IsReversal,
    string Description,
    DateTime CreatedAt);

/// <summary>GET /stock-trans (and /stocks/{id}/transactions): filters stockId, invoiceId, direction, dateFrom, dateTo, q.</summary>
public sealed class StockTransListQuery : PagedRequest
{
    public int? StockId { get; set; }
    public int? InvoiceId { get; set; }
    public StockTransDirection? Direction { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
}

/// <summary>GET /act-trans (and /current-accounts/{id}/transactions, /me/transactions).</summary>
public sealed class ActTransListQuery : PagedRequest
{
    public int? CurrentAccountId { get; set; }
    public int? InvoiceId { get; set; }
    public ActTransDirection? Direction { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
}

public static class TransactionMappings
{
    public static readonly Expression<Func<StockTrans, StockTransDto>> StockProjection = t => new StockTransDto(
        t.Id, t.CompanyId, t.StockId, t.Stock!.Code, t.Stock.Name, t.Stock.Unit, t.InvoiceId, t.Invoice!.DocumentNo,
        t.TransDate, t.Direction, t.Quantity, t.UnitPrice, t.Amount, t.IsReversal, t.Description, t.CreatedAt);

    public static readonly Expression<Func<ActTrans, ActTransDto>> ActProjection = t => new ActTransDto(
        t.Id, t.CompanyId, t.CurrentAccountId, t.CurrentAccount!.Code, t.CurrentAccount.Title, t.InvoiceId, t.Invoice!.DocumentNo,
        t.TransDate, t.Direction, t.Amount, t.IsReversal, t.Description, t.CreatedAt);
}
