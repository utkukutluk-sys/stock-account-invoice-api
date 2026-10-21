using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace StockAccount.Application.Common.Paging;

/// <summary>
/// Injection-safe dynamic sorting and paging for EF Core queries. Sort fields are resolved through a
/// <see cref="SortMap{T}"/> whitelist of expression trees, so EF generates the ORDER BY from typed
/// member access — no client string ever becomes a column name or SQL fragment.
/// </summary>
public static class QueryableSortingExtensions
{
    /// <summary>
    /// Applies "field,-field2" sorting (null/blank → map default) and appends the id tie-breaker (asc)
    /// unless the id field is already part of the sort. Throws <see cref="InvalidSortFieldException"/> for unknown fields.
    /// </summary>
    public static IOrderedQueryable<T> ApplySort<T>(this IQueryable<T> source, string? sort, SortMap<T> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);

        var effective = string.IsNullOrWhiteSpace(sort) ? map.DefaultSort : sort;
        var expression = source.Expression;
        var first = true;
        var hasId = false;

        foreach (var (field, descending) in SortMap<T>.Parse(effective))
        {
            if (!map.TryGet(field, out var selector))
            {
                throw new InvalidSortFieldException(field);
            }

            hasId |= ReferenceEquals(selector, map.IdSelector);
            expression = CallOrder(expression, selector, first, descending);
            first = false;
        }

        if (!hasId)
        {
            expression = CallOrder(expression, map.IdSelector, first, descending: false);
        }

        return (IOrderedQueryable<T>)source.Provider.CreateQuery<T>(expression);

        static MethodCallExpression CallOrder(Expression current, LambdaExpression key, bool isFirst, bool descending)
        {
            var method = (isFirst, descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };

            return Expression.Call(
                typeof(Queryable),
                method,
                [typeof(T), key.ReturnType],
                current,
                Expression.Quote(key));
        }
    }

    /// <summary>Counts, then fetches one page. Call after filtering + <see cref="ApplySort{T}"/> (+ optional Select projection).</summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> source, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var total = await source.CountAsync(cancellationToken);
        var items = total == 0
            ? []
            : await source.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return new PagedResult<T>(items, page, pageSize, total);
    }
}
