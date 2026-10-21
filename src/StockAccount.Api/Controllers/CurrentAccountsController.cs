using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.CurrentAccounts;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Current accounts / Cari (CONTRACT §5.8). Reads are cached (X-Cache header); delete is admin only.</summary>
[Route("api/current-accounts")]
[Tags("CurrentAccounts")]
[Authorize(Policy = Policies.Operator)]
public sealed class CurrentAccountsController(ICurrentAccountService accounts) : ApiControllerBase
{
    /// <summary>Lists accounts. Filters: q (code, title, taxNumber), type, city, isActive. Sort: code,title,type,city,balance,createdAt.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<CurrentAccountDto>>> List([FromQuery] CurrentAccountListQuery query, CancellationToken cancellationToken) =>
        Cached(await accounts.ListAsync(query, cancellationToken));

    /// <summary>Gets an account (cached).</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentAccountDto>> Get(int id, CancellationToken cancellationToken) =>
        Cached(await accounts.GetAsync(id, cancellationToken));

    /// <summary>Creates an account. 409 DUPLICATE_CODE.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<CurrentAccountDto>> Create([FromBody] CurrentAccountSaveRequest request, CancellationToken cancellationToken)
    {
        var dto = await accounts.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Updates an account (rowVersion required).</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<CurrentAccountDto>> Update(int id, [FromBody] CurrentAccountSaveRequest request, CancellationToken cancellationToken) =>
        Ok(await accounts.UpdateAsync(id, request, cancellationToken));

    /// <summary>Soft-deletes an account. Admin only. 409 ACCOUNT_HAS_BALANCE / ACCOUNT_IN_USE.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await accounts.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Account movements. Filters: direction, dateFrom, dateTo. Default sort -transDate,-id.</summary>
    [HttpGet("{id:int}/transactions")]
    public async Task<ActionResult<PagedResult<ActTransDto>>> Transactions(int id, [FromQuery] ActTransListQuery query, CancellationToken cancellationToken) =>
        Ok(await accounts.ListTransactionsAsync(id, query, cancellationToken));
}
