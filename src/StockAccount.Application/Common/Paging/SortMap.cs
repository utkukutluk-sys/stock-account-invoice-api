using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace StockAccount.Application.Common.Paging;

/// <summary>
/// Whitelist of sortable fields for an entity/projection: API field name → strongly typed key selector.
/// The client's <c>sort</c> string is only ever used as a dictionary key; it never reaches SQL, so it
/// cannot inject anything. Unknown fields are rejected (→ 400 VALIDATION_FAILED).
/// Build once per entity and keep it in a static field (it is immutable after construction).
/// </summary>
/// <example>
/// <code>
/// public static readonly SortMap&lt;Stock&gt; StockSorts = new SortMap&lt;Stock&gt;("code", s =&gt; s.Id)
///     .Add("code", s =&gt; s.Code)
///     .Add("name", s =&gt; s.Name)
///     .Add("balance", s =&gt; s.Balance)
///     .Add("createdAt", s =&gt; s.CreatedAt);
/// </code>
/// </example>
public sealed class SortMap<T>
{
    private readonly Dictionary<string, LambdaExpression> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="defaultSort">Sort used when the request has none, e.g. "code" or "-transDate,-id".</param>
    /// <param name="idSelector">Stable tie-breaker (id asc) appended unless the sort already contains the id field.
    /// It is also registered as sortable field "id".</param>
    public SortMap(string defaultSort, Expression<Func<T, int>> idSelector)
        : this(defaultSort, (LambdaExpression)idSelector)
    {
    }

    /// <inheritdoc cref="SortMap{T}(string, Expression{Func{T, int}})"/>
    public SortMap(string defaultSort, Expression<Func<T, long>> idSelector)
        : this(defaultSort, (LambdaExpression)idSelector)
    {
    }

    private SortMap(string defaultSort, LambdaExpression idSelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultSort);
        DefaultSort = defaultSort;
        IdSelector = idSelector;
        _fields[IdField] = idSelector;
    }

    public const string IdField = "id";

    public string DefaultSort { get; }

    internal LambdaExpression IdSelector { get; }

    public IEnumerable<string> Fields => _fields.Keys;

    public SortMap<T> Add<TKey>(string field, Expression<Func<T, TKey>> keySelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(keySelector);
        _fields[field] = keySelector;
        return this;
    }

    internal bool TryGet(string field, [NotNullWhen(true)] out LambdaExpression? selector) =>
        _fields.TryGetValue(field, out selector);

    /// <summary>Parses "a,-b" into (field, descending) pairs; returns false with the first unknown field.
    /// Use from FluentValidation: <c>RuleFor(x =&gt; x.Sort).Must(s =&gt; map.IsValid(s, out _))</c>.</summary>
    public bool IsValid(string? sort, out string? invalidField)
    {
        foreach (var (field, _) in Parse(sort ?? DefaultSort))
        {
            if (!_fields.ContainsKey(field))
            {
                invalidField = field;
                return false;
            }
        }

        invalidField = null;
        return true;
    }

    internal static IEnumerable<(string Field, bool Descending)> Parse(string sort)
    {
        foreach (var raw in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var descending = raw.StartsWith('-');
            var field = raw.TrimStart('-', '+').Trim();
            if (field.Length > 0)
            {
                yield return (field, descending);
            }
        }
    }
}
