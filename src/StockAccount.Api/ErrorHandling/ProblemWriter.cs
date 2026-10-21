using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using StockAccount.Api.Middleware;
using StockAccount.Application.Common.Exceptions;

namespace StockAccount.Api.ErrorHandling;

/// <summary>Builds and writes contract ProblemDetails (CONTRACT §5.2): type, title, status, detail, instance, code, traceId, correlationId, errors.</summary>
public static class ProblemWriter
{
    public static ProblemDetails Create(HttpContext http, int status, string code, string? detail, IDictionary<string, string[]>? errors = null)
    {
        ProblemDetails problem = errors is null
            ? new ProblemDetails()
            : new ValidationProblemDetails(errors);
        problem.Status = status;
        problem.Detail = detail;
        problem.Extensions["code"] = code;
        Enrich(http, problem);
        return problem;
    }

    public static Task WriteAsync(HttpContext http, int status, string code, string? detail, IDictionary<string, string[]>? errors = null)
    {
        var problem = Create(http, status, code, detail, errors);
        http.Response.StatusCode = status;
        var service = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        return service.WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem }).AsTask();
    }

    /// <summary>Fills the common members; also used by <c>CustomizeProblemDetails</c> for framework-generated problems.</summary>
    public static void Enrich(HttpContext http, ProblemDetails problem)
    {
        var status = problem.Status ?? http.Response.StatusCode;
        problem.Status = status;
        problem.Type = $"https://httpstatuses.io/{status}";
        problem.Title ??= TitleFor(status);
        problem.Instance ??= http.Request.Path;
        if (!problem.Extensions.ContainsKey("code"))
        {
            problem.Extensions["code"] = DefaultCode(status);
        }

        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.Get(http);
    }

    /// <summary>"Lines[0].Quantity" → "lines[0].quantity"; "$.lines[0].quantity" → "lines[0].quantity".</summary>
    public static string ToCamelPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var trimmed = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
        var sb = new StringBuilder(trimmed.Length);
        var startOfSegment = true;
        foreach (var ch in trimmed)
        {
            sb.Append(startOfSegment ? char.ToLowerInvariant(ch) : ch);
            startOfSegment = ch == '.';
        }

        return sb.ToString();
    }

    private static string DefaultCode(int status) => status switch
    {
        401 => ErrorCodes.Unauthorized,
        403 => ErrorCodes.Forbidden,
        404 or 405 => ErrorCodes.NotFound,
        409 => ErrorCodes.ConcurrencyConflict,
        >= 500 => ErrorCodes.InternalError,
        _ => ErrorCodes.ValidationFailed,
    };

    private static string TitleFor(int status) => status switch
    {
        400 => "Validation failed",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        422 => "Business rule violation",
        >= 500 => "Internal server error",
        _ => "Request failed",
    };
}
