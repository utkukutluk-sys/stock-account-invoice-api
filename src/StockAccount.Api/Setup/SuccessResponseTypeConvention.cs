using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace StockAccount.Api.Setup;

/// <summary>
/// The base controller declares error responses with <see cref="ProducesResponseTypeAttribute"/>, which stops ApiExplorer
/// from inferring the 200 response of <c>ActionResult&lt;T&gt;</c> actions. This convention adds the success response
/// (200 with T, or the declared 2xx status with T) to every action so Swagger documents the schema (BUG-3).
/// </summary>
public sealed class SuccessResponseTypeConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var action in application.Controllers.SelectMany(c => c.Actions))
        {
            ApplyToAction(action);
        }
    }

    private static void ApplyToAction(ActionModel action)
    {
        var resultType = UnwrapActionResult(action.ActionMethod.ReturnType);
        if (resultType is null)
        {
            return;
        }

        var declared = action.Filters.OfType<ProducesResponseTypeAttribute>()
            .Concat(action.Attributes.OfType<ProducesResponseTypeAttribute>())
            .Where(a => a.StatusCode is >= 200 and < 300)
            .ToList();

        if (declared.Count == 0)
        {
            action.Filters.Add(new ProducesResponseTypeAttribute(resultType, StatusCodes.Status200OK));
            return;
        }

        foreach (var attribute in declared.Where(a => a.Type == typeof(void) && a.StatusCode != StatusCodes.Status204NoContent))
        {
            attribute.Type = resultType;
        }
    }

    /// <summary>Task&lt;ActionResult&lt;T&gt;&gt; / ActionResult&lt;T&gt; → T; anything else → null.</summary>
    private static Type? UnwrapActionResult(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            type = type.GetGenericArguments()[0];
        }

        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>)
            ? type.GetGenericArguments()[0]
            : null;
    }
}
