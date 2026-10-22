namespace StockAccount.Api.Middleware;

/// <summary>
/// Security headers on every response: nosniff, no framing, no referrer, minimal permissions, and a locked-down CSP for
/// API/JSON responses (Swagger UI keeps its own CSP-free page because it needs inline scripts/styles).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            headers.Remove("X-Powered-By");
            headers.Remove("Server");
            return Task.CompletedTask;
        });

        return next(context);
    }
}
