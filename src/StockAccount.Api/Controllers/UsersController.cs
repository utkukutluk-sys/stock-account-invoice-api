using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Users;
using StockAccount.Application.Features.Users.Dtos;

namespace StockAccount.Api.Controllers;

/// <summary>User administration (CONTRACT §5.6). Admin only.</summary>
[Route("api/users")]
[Tags("Users")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class UsersController(IUserService users) : ApiControllerBase
{
    /// <summary>Lists users. Filters: q (email, fullName), role, companyId, isActive. Sort: email,fullName,createdAt,lastLoginAt.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<UserDto>>> List([FromQuery] UserListQuery query, CancellationToken cancellationToken) =>
        Ok(await users.ListAsync(query, cancellationToken));

    /// <summary>Gets a user.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserDto>> Get(int id, CancellationToken cancellationToken) =>
        Ok(await users.GetAsync(id, cancellationToken));

    /// <summary>Creates a user. 409 DUPLICATE_EMAIL / ACCOUNT_IN_USE.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> Create([FromBody] UserCreateRequest request, CancellationToken cancellationToken)
    {
        var dto = await users.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Updates a user; deactivation revokes refresh tokens; newPassword resets the password.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> Update(int id, [FromBody] UserUpdateRequest request, CancellationToken cancellationToken) =>
        Ok(await users.UpdateAsync(id, request, cancellationToken));

    /// <summary>Deletes a user (hard delete). 409 CANNOT_DELETE_SELF.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await users.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
