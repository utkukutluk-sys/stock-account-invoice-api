using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Dashboard;
using StockAccount.Application.Features.Dashboard.Dtos;
using StockAccount.Application.Features.Transactions;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Stock ledger (read-only; rows are written only by invoice approve/cancel).</summary>
[Route("api/stock-trans")]
[Tags("Transactions")]
[Authorize(Policy = Policies.Operator)]
public sealed class StockTransController(ITransactionService transactions) : ApiControllerBase
{
    /// <summary>Lists stock movements. Filters: stockId, invoiceId, direction, dateFrom, dateTo, q. Sort: transDate,quantity,amount,createdAt (default -transDate,-id).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<StockTransDto>>> List([FromQuery] StockTransListQuery query, CancellationToken cancellationToken) =>
        Ok(await transactions.ListStockTransAsync(query, cancellationToken));
}

/// <summary>Current account ledger (read-only).</summary>
[Route("api/act-trans")]
[Tags("Transactions")]
[Authorize(Policy = Policies.Operator)]
public sealed class ActTransController(ITransactionService transactions) : ApiControllerBase
{
    /// <summary>Lists account movements. Filters: currentAccountId, invoiceId, direction, dateFrom, dateTo, q. Sort: transDate,amount,createdAt (default -transDate,-id).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<ActTransDto>>> List([FromQuery] ActTransListQuery query, CancellationToken cancellationToken) =>
        Ok(await transactions.ListActTransAsync(query, cancellationToken));
}

/// <summary>Dashboard summary.</summary>
[Route("api/dashboard")]
[Tags("Dashboard")]
[Authorize(Policy = Policies.Operator)]
public sealed class DashboardController(IDashboardService dashboard) : ApiControllerBase
{
    /// <summary>Counts, monthly totals (Approved, UTC month by InvoiceDate), receivable/payable, 5 lowest stocks, 5 recent invoices.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await dashboard.GetSummaryAsync(cancellationToken));
}
