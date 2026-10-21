using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace StockAccount.Api.Middleware;

/// <summary>
/// CONTRACT §5.3: every GET on <c>/api/**</c> (except <c>/api/auth/**</c>) that returns 200 JSON gets a weak ETag
/// <c>W/"&lt;first 16 hex of SHA-256 of body&gt;"</c> and <c>Cache-Control: private, no-cache</c>; a matching
/// <c>If-None-Match</c> yields 304 with an empty body. Implemented as middleware (buffers the body of those GETs only).
/// </summary>
public sealed class ETagMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!HttpMethods.IsGet(context.Request.Method)
            || !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var response = context.Response;
        if (response.StatusCode == StatusCodes.Status200OK
            && buffer.Length > 0
            && response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var etag = Compute(buffer);
            response.Headers.ETag = etag;
            response.Headers.CacheControl = "private, no-cache";

            if (Matches(context.Request.Headers.IfNoneMatch, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                response.Headers.ContentType = default;
                return;
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);
    }

    /// <summary>Weak ETag over the response bytes.</summary>
    public static string Compute(MemoryStream body)
    {
        var hash = SHA256.HashData(body.GetBuffer().AsSpan(0, (int)body.Length));
        return $"W/\"{Convert.ToHexStringLower(hash)[..16]}\"";
    }

    /// <summary>Weak comparison against a (possibly comma separated) If-None-Match header.</summary>
    public static bool Matches(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string etag)
    {
        if (ifNoneMatch.Count == 0)
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseList(ifNoneMatch, out var tags))
        {
            return false;
        }

        var current = EntityTagHeaderValue.Parse(etag);
        return tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(current, useStrongComparison: false));
    }
}
