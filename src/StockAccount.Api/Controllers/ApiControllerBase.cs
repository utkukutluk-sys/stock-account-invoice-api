using Microsoft.AspNetCore.Mvc;
using StockAccount.Application.Common.Caching;

namespace StockAccount.Api.Controllers;

/// <summary>Common base: JSON in/out, ProblemDetails for errors.</summary>
[ApiController]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
public abstract class ApiControllerBase : ControllerBase
{
    public const string CacheHeader = "X-Cache";

    /// <summary>Returns 200 with the value and sets <c>X-Cache: HIT|MISS</c> (CONTRACT §5.3).</summary>
    protected ActionResult<T> Cached<T>(Cached<T> cached)
    {
        Response.Headers[CacheHeader] = cached.Hit ? "HIT" : "MISS";
        return Ok(cached.Value);
    }
}
