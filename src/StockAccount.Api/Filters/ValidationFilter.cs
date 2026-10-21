using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StockAccount.Api.Filters;

/// <summary>
/// Runs the registered FluentValidation validator for every action argument (body and [FromQuery] objects) and throws
/// <see cref="ValidationException"/> (→ 400 VALIDATION_FAILED with camelCase <c>errors</c>) before the action runs.
/// </summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is IValidator validator)
            {
                var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
                if (!result.IsValid)
                {
                    throw new ValidationException(result.Errors);
                }
            }
        }

        await next();
    }
}
