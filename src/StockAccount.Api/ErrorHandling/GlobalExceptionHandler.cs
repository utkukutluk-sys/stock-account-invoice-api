using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Paging;
using StockAccount.Domain.Common;

namespace StockAccount.Api.ErrorHandling;

/// <summary>Maps exceptions to contract ProblemDetails (CONTRACT §5.2). No stack traces outside Development.</summary>
public sealed class GlobalExceptionHandler(IHostEnvironment environment, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public const string ValidationDetail = "Bir veya daha fazla alan geçersiz.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499; // client closed request; nothing to write
            return true;
        }

        var (status, code, detail, errors) = Map(exception);

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("Request {Method} {Path} failed with {Status} {Code}: {Detail}", httpContext.Request.Method, httpContext.Request.Path, status, code, detail);
        }

        var problem = ProblemWriter.Create(httpContext, status, code, detail, errors);
        if (status >= 500 && environment.IsDevelopment())
        {
            problem.Extensions["exception"] = exception.ToString();
        }

        httpContext.Response.StatusCode = status;
        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }

    internal static (int Status, string Code, string Detail, IDictionary<string, string[]>? Errors) Map(Exception exception) => exception switch
    {
        ValidationException v => (400, ErrorCodes.ValidationFailed, v.Errors.FirstOrDefault()?.ErrorMessage ?? ValidationDetail, ToErrors(v)),
        InvalidSortFieldException s => (400, ErrorCodes.ValidationFailed, s.Message, new Dictionary<string, string[]> { ["sort"] = [s.Message] }),
        AppException a => (a.StatusCode, a.Code, a.Message, null),
        DomainException d => (ErrorCodes.StatusFor(d.Code), d.Code, d.Message, null),
        DbUpdateConcurrencyException => (409, ErrorCodes.ConcurrencyConflict, "Kayıt başka bir kullanıcı tarafından değiştirildi. Lütfen sayfayı yenileyip tekrar deneyin.", null),
        BadHttpRequestException b => (b.StatusCode, ErrorCodes.ValidationFailed, b.Message, null),
        _ => (500, ErrorCodes.InternalError, "Beklenmeyen bir hata oluştu.", null),
    };

    private static Dictionary<string, string[]> ToErrors(ValidationException v) =>
        v.Errors
            .GroupBy(e => ProblemWriter.ToCamelPath(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
}
