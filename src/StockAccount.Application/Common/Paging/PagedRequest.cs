using FluentValidation;

namespace StockAccount.Application.Common.Paging;

/// <summary>Common list query parameters (CONTRACT §5.1). Derived classes add entity filters.</summary>
public class PagedRequest
{
    public const int MaxPageSize = 100;

    /// <summary>Upper bound for <see cref="Page"/> so (page - 1) * pageSize always fits in an int OFFSET (BUG-8).</summary>
    public const int MaxPage = 1_000_000;

    /// <summary>1-based page number (default 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size 1..100 (default 20).</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>Comma separated fields, "-" prefix = descending (e.g. <c>-invoiceDate,documentNo</c>).</summary>
    public string? Sort { get; set; }

    /// <summary>Free-text "contains" search.</summary>
    public string? Q { get; set; }

    /// <summary>Trimmed search text or null when blank.</summary>
    internal string? Search => string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();
}

/// <summary>Base validator for list queries: paging bounds and (optionally) the sort whitelist.</summary>
public abstract class PagedRequestValidator<T> : AbstractValidator<T>
    where T : PagedRequest
{
    protected PagedRequestValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, PagedRequest.MaxPage)
            .WithMessage($"Sayfa numarası 1 ile {PagedRequest.MaxPage:N0} arasında olmalıdır.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, PagedRequest.MaxPageSize)
            .WithMessage($"Sayfa boyutu 1 ile {PagedRequest.MaxPageSize} arasında olmalıdır.");
        RuleFor(x => x.Q).MaximumLength(200).WithMessage("Arama metni en fazla 200 karakter olabilir.");
    }

    /// <summary>Adds the sort whitelist rule (unknown field → 400 with errors["sort"]).</summary>
    protected void ValidateSort<TEntity>(SortMap<TEntity> map) =>
        RuleFor(x => x.Sort)
            .Must(s => map.IsValid(s, out _))
            .WithMessage(x => map.IsValid(x.Sort, out var field) ? string.Empty : $"Geçersiz sıralama alanı: {field}");
}

/// <summary>Shared date range rule for list filters.</summary>
public static class DateRangeRules
{
    public static bool IsValid(DateOnly? from, DateOnly? to) => from is null || to is null || from <= to;
}
