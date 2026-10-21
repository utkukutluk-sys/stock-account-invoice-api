using StockAccount.Application.Common.Paging;

namespace StockAccount.Application.Features.Portal.Dtos;

/// <summary>GET /me/transactions query: list params + dateFrom, dateTo (default sort -transDate,-id).</summary>
public sealed class PortalTransactionListQuery : PagedRequest
{
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
}
