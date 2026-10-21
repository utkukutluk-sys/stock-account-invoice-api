using Serilog.Context;

namespace StockAccount.Api.Middleware;

/// <summary>
/// Reads <c>X-Correlation-Id</c> (generated when absent or invalid), echoes it on the response and pushes it into the
/// Serilog LogContext for the whole request (CONTRACT §5.3, §7).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>Pushes UserId / CompanyId of the authenticated caller into the LogContext (after authentication).</summary>
public sealed class UserLogContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        using (LogContext.PushProperty("UserId", user.FindFirst("sub")?.Value))
        using (LogContext.PushProperty("CompanyId", user.FindFirst("company_id")?.Value ?? context.Request.Headers["X-Company-Id"].ToString()))
        {
            await next(context);
        }
    }
}
