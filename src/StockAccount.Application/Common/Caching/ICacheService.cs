namespace StockAccount.Application.Common.Caching;

/// <summary>
/// Distributed cache abstraction (CONTRACT §6). Implementations must catch and log every cache error
/// (Warning): a cache failure never fails a request (reads become misses, writes are skipped).
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
        where T : class;

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Current list version token for <c>{entity}:ver:{scope}</c>; created (Guid "N", 24 h sliding) when missing.</summary>
    Task<string> GetVersionAsync(string entity, string scope, CancellationToken cancellationToken = default);

    /// <summary>Replaces the version token so every list key of that entity/scope becomes unreachable.</summary>
    Task BumpVersionAsync(string entity, string scope, CancellationToken cancellationToken = default);
}

/// <summary>A value plus whether it came from the cache (drives the <c>X-Cache: HIT|MISS</c> header).</summary>
public sealed record Cached<T>(T Value, bool Hit);

/// <summary>Configuration section "Cache" (CONTRACT §6, §8.1).</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public bool Enabled { get; set; } = true;
    public bool UseRedis { get; set; } = true;
    public int EntityTtlMinutes { get; set; } = 10;
    public int ListTtlMinutes { get; set; } = 5;

    public TimeSpan EntityTtl => TimeSpan.FromMinutes(EntityTtlMinutes);
    public TimeSpan ListTtl => TimeSpan.FromMinutes(ListTtlMinutes);
}
