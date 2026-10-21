using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Invoices;
using StockAccount.Application.Features.Invoices.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Invoices (CONTRACT §5.9): Draft CRUD, approve (ledger + balances), cancel (admin, reversal rows).</summary>
[Route("api/invoices")]
[Tags("Invoices")]
[Authorize(Policy = Policies.Operator)]
public sealed class InvoicesController(IInvoiceService invoices) : ApiControllerBase
{
    /// <summary>Lists invoices. Filters: q, type, status, currentAccountId, series, dateFrom, dateTo. Sort: invoiceDate,documentNo,grandTotal,status,createdAt (default -invoiceDate,-id).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<InvoiceListItemDto>>> List([FromQuery] InvoiceListQuery query, CancellationToken cancellationToken) =>
        Ok(await invoices.ListAsync(query, cancellationToken));

    /// <summary>Gets an invoice with its lines.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceDto>> Get(int id, CancellationToken cancellationToken) =>
        Ok(await invoices.GetAsync(id, cancellationToken));

    /// <summary>Creates a Draft invoice. Totals are computed by the server.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] InvoiceSaveRequest request, CancellationToken cancellationToken)
    {
        var dto = await invoices.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Replaces header and all lines of a Draft invoice (rowVersion required). 409 INVOICE_NOT_DRAFT.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InvoiceDto>> Update(int id, [FromBody] InvoiceSaveRequest request, CancellationToken cancellationToken) =>
        Ok(await invoices.UpdateAsync(id, request, cancellationToken));

    /// <summary>Deletes a Draft invoice (hard delete). 409 INVOICE_NOT_DRAFT.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await invoices.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Approves a Draft invoice: assigns the number, writes StockTrans/ActTrans and updates balances.</summary>
    /// <response code="409">INVOICE_NOT_DRAFT or CONCURRENCY_CONFLICT.</response>
    /// <response code="422">INSUFFICIENT_STOCK, INVOICE_HAS_NO_LINES, INACTIVE_STOCK, INACTIVE_ACCOUNT, ACCOUNT_TYPE_MISMATCH.</response>
    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvoiceDto>> Approve(int id, [FromBody] InvoiceApproveRequest request, CancellationToken cancellationToken) =>
        Ok(await invoices.ApproveAsync(id, request, cancellationToken));

    /// <summary>Cancels an Approved invoice (admin only): writes reversal rows and restores balances.</summary>
    /// <response code="409">INVOICE_NOT_APPROVED or CONCURRENCY_CONFLICT.</response>
    /// <response code="422">INSUFFICIENT_STOCK (cancelling a purchase whose goods are gone).</response>
    [HttpPost("{id:int}/cancel")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvoiceDto>> Cancel(int id, [FromBody] InvoiceCancelRequest request, CancellationToken cancellationToken) =>
        Ok(await invoices.CancelAsync(id, request, cancellationToken));
}
