using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Companies;
using StockAccount.Application.Features.Companies.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>Companies (tenants). Admin only, except <c>/companies/current</c> (CONTRACT §5.5).</summary>
[Route("api/companies")]
[Tags("Companies")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class CompaniesController(ICompanyService companies) : ApiControllerBase
{
    /// <summary>Lists companies. Filters: q (name, taxNumber, city), isActive. Sort: name,taxNumber,city,createdAt (default name).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<CompanyDto>>> List([FromQuery] CompanyListQuery query, CancellationToken cancellationToken) =>
        Ok(await companies.ListAsync(query, cancellationToken));

    /// <summary>Gets a company.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanyDto>> Get(int id, CancellationToken cancellationToken) =>
        Ok(await companies.GetAsync(id, cancellationToken));

    /// <summary>The caller's company (admin: the X-Company-Id company, otherwise 404).</summary>
    [HttpGet("current")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ActionResult<CompanyDto>> Current(CancellationToken cancellationToken) =>
        Ok(await companies.GetCurrentAsync(cancellationToken));

    /// <summary>Creates a company. 409 DUPLICATE_TAX_NUMBER.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<CompanyDto>> Create([FromBody] CompanySaveRequest request, CancellationToken cancellationToken)
    {
        var dto = await companies.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Updates a company (rowVersion required). 409 CONCURRENCY_CONFLICT / DUPLICATE_TAX_NUMBER.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<CompanyDto>> Update(int id, [FromBody] CompanySaveRequest request, CancellationToken cancellationToken) =>
        Ok(await companies.UpdateAsync(id, request, cancellationToken));

    /// <summary>Soft-deletes a company. 409 COMPANY_NOT_EMPTY.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await companies.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
