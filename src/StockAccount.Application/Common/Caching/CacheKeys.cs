using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace StockAccount.Application.Common.Caching;

/// <summary>
/// Cache key builder (CONTRACT §6). Keys are relative to the distributed cache instance prefix <c>sa:</c>.
/// </summary>
public static class CacheKeys
{
    public const string Stock = "stock";
    public const string Account = "account";
    public const string AllScope = "all";
    public static readonly TimeSpan VersionSlidingTtl = TimeSpan.FromHours(24);

    /// <summary><c>{entity}:{id}</c></summary>
    public static string Entity(string entity, int id) => string.Create(CultureInfo.InvariantCulture, $"{entity}:{id}");

    /// <summary><c>{entity}:ver:{scope}</c></summary>
    public static string Version(string entity, string scope) => $"{entity}:ver:{scope}";

    /// <summary><c>{entity}:list:{scope}:{verToken}:{queryHash}</c></summary>
    public static string List(string entity, string scope, string versionToken, string queryHash) =>
        $"{entity}:list:{scope}:{versionToken}:{queryHash}";

    /// <summary>
    /// First 16 hex chars of SHA-256 over the normalized query: public readable properties of the query object,
    /// lower-cased names, sorted ordinally, invariant-formatted values (defaults are already filled in by the
    /// query object; pass <paramref name="effectiveSort"/> so an omitted sort hashes like the default sort).
    /// The same parameters in a different order always give the same hash.
    /// </summary>
    public static string QueryHash(object query, string? effectiveSort = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in query.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            parts[property.Name.ToLowerInvariant()] = Format(property.GetValue(query));
        }

        if (effectiveSort is not null)
        {
            parts["sort"] = effectiveSort.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        }

        if (parts.TryGetValue("q", out var q))
        {
            parts["q"] = q.Trim();
        }

        var normalized = string.Join('&', parts.Select(p => p.Key + "=" + p.Value));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => string.Join(',', items.Cast<object?>().Select(Format)),
        _ => value.ToString() ?? string.Empty,
    };
}
