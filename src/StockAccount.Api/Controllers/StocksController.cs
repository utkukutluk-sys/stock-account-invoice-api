using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Stocks;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Stock cards (CONTRACT §5.7). Reads are cached (X-Cache header); delete is admin only.</summary>
[Route("api/stocks")]
[Tags("Stocks")]
[Authorize(Policy = Policies.Operator)]
public sealed class StocksController(IStockService stocks) : ApiControllerBase
{
    /// <summary>Lists stocks. Filters: q (code, name, barcode), unit, isActive. Sort: code,name,unit,unitPrice,balance,createdAt (default code).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<StockDto>>> List([FromQuery] StockListQuery query, CancellationToken cancellationToken) =>
        Cached(await stocks.ListAsync(query, cancellationToken));

    /// <summary>Gets a stock (cached).</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StockDto>> Get(int id, CancellationToken cancellationToken) =>
        Cached(await stocks.GetAsync(id, cancellationToken));

    /// <summary>Creates a stock in the caller's company (admin: X-Company-Id required). 409 DUPLICATE_CODE.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<StockDto>> Create([FromBody] StockSaveRequest request, CancellationToken cancellationToken)
    {
        var dto = await stocks.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Updates a stock (rowVersion required). Balance is never accepted from input.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<StockDto>> Update(int id, [FromBody] StockSaveRequest request, CancellationToken cancellationToken) =>
        Ok(await stocks.UpdateAsync(id, request, cancellationToken));

    /// <summary>Soft-deletes a stock. Admin only. 409 STOCK_HAS_BALANCE / STOCK_IN_USE.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await stocks.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Stock movements of one stock. Filters: direction, dateFrom, dateTo. Default sort -transDate,-id.</summary>
    [HttpGet("{id:int}/transactions")]
    public async Task<ActionResult<PagedResult<StockTransDto>>> Transactions(int id, [FromQuery] StockTransListQuery query, CancellationToken cancellationToken) =>
        Ok(await stocks.ListTransactionsAsync(id, query, cancellationToken));
}
