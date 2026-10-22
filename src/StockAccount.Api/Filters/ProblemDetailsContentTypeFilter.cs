using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StockAccount.Api.Filters;

/// <summary>
/// MVC-generated client errors (415 from input formatters / [Consumes], 404 NotFound(), …) are ObjectResults carrying
/// ProblemDetails. The controllers' <c>[Produces("application/json")]</c> would force them to application/json; this
/// always-run filter (ordered after Produces) restores <c>application/problem+json</c> (BUG-10).
/// </summary>
public sealed class ProblemDetailsContentTypeFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    public int Order => 10_000;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails } result)
        {
            result.ContentTypes.Clear();
            result.ContentTypes.Add("application/problem+json");
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
