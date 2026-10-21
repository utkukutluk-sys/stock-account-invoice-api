using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Application.Features.Portal;
using StockAccount.Application.Features.Portal.Dtos;
using StockAccount.Application.Features.Reconciliation;
using StockAccount.Application.Features.Reconciliation.Dtos;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Portal endpoints for users linked to a current account (CONTRACT §5.10).</summary>
[Route("api/me")]
[Tags("Portal")]
[Authorize(Policy = Policies.Portal)]
public sealed class MeController(IPortalService portal) : ApiControllerBase
{
    /// <summary>Own current account (with balance).</summary>
    [HttpGet("account")]
    public async Task<ActionResult<CurrentAccountDto>> Account(CancellationToken cancellationToken) =>
        Ok(await portal.GetAccountAsync(cancellationToken));

    /// <summary>Own account movements. Filters: dateFrom, dateTo.</summary>
    [HttpGet("transactions")]
    public async Task<ActionResult<PagedResult<ActTransDto>>> Transactions([FromQuery] PortalTransactionListQuery query, CancellationToken cancellationToken) =>
        Ok(await portal.ListTransactionsAsync(query, cancellationToken));

    /// <summary>Own invoices (Approved/Cancelled only). Filter: status.</summary>
    [HttpGet("invoices")]
    public async Task<ActionResult<PagedResult<InvoiceListItemDto>>> Invoices([FromQuery] PortalInvoiceListQuery query, CancellationToken cancellationToken) =>
        Ok(await portal.ListInvoicesAsync(query, cancellationToken));

    /// <summary>Own invoice detail (404 if not own or Draft).</summary>
    [HttpGet("invoices/{id:int}")]
    public async Task<ActionResult<InvoiceDto>> Invoice(int id, CancellationToken cancellationToken) =>
        Ok(await portal.GetInvoiceAsync(id, cancellationToken));
}

/// <summary>Administrative operations.</summary>
[Route("api/admin")]
[Tags("Admin")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class AdminController(IReconciliationService reconciliation) : ApiControllerBase
{
    /// <summary>Runs the balance reconciliation now (all companies). <c>fix=true</c> corrects stored balances to the ledger.</summary>
    [HttpPost("reconciliation/run")]
    public async Task<ActionResult<ReconciliationResultDto>> RunReconciliation([FromQuery] bool fix = false, CancellationToken cancellationToken = default) =>
        Ok(await reconciliation.RunAsync(fix, cancellationToken));
}
