using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using StockAccount.Application.Common.Exceptions;

namespace StockAccount.Api.ErrorHandling;

/// <summary>
/// Model-binding / JSON errors → contract ProblemDetails (400 VALIDATION_FAILED, application/problem+json) with Turkish
/// messages and camelCase keys. Framework texts (type names, byte positions) are never returned (BUG-2).
/// </summary>
public static class ModelStateProblemFactory
{
    public const string BodyKey = "body";

    public static IActionResult Create(ActionContext context)
    {
        var parameterNames = context.ActionDescriptor.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (key, entry) in context.ModelState)
        {
            if (entry.Errors.Count == 0)
            {
                continue;
            }

            var path = ProblemWriter.ToCamelPath(key);
            var isBodyLevel = string.IsNullOrEmpty(path) || path == "$" || parameterNames.Contains(path);
            var name = isBodyLevel ? BodyKey : path;
            var messages = entry.Errors.Select(e => Translate(e, isBodyLevel)).Distinct().ToArray();
            errors[name] = errors.TryGetValue(name, out var existing) ? existing.Concat(messages).Distinct().ToArray() : messages;
        }

        // "The request field is required." only repeats a field error: drop it when there are field-level errors.
        if (errors.Count > 1)
        {
            errors.Remove(BodyKey);
        }

        if (errors.Count == 0)
        {
            errors[BodyKey] = ["İstek gövdesi geçersiz."];
        }

        var detail = errors.First().Value.First();
        var problem = ProblemWriter.Create(context.HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, detail, errors);

        // JsonResult (not ObjectResult) so the controllers' [Produces("application/json")] cannot override the media type.
        return new JsonResult(problem) { StatusCode = StatusCodes.Status400BadRequest, ContentType = "application/problem+json" };
    }

    private static string Translate(ModelError error, bool isBodyLevel)
    {
        var message = error.ErrorMessage ?? string.Empty;
        if (isBodyLevel)
        {
            return error.Exception is not null || message.Contains("JSON", StringComparison.OrdinalIgnoreCase)
                ? "İstek gövdesi geçerli bir JSON değil."
                : "İstek gövdesi eksik veya geçersiz.";
        }

        if (message.Contains("required", StringComparison.OrdinalIgnoreCase))
        {
            return "Bu alan zorunludur.";
        }

        return "Geçersiz değer veya biçim.";
    }
}
