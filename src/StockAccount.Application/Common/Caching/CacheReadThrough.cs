namespace StockAccount.Application.Common.Caching;

/// <summary>Read-through helper: cache hit → value (Hit = true); miss → factory, then store.</summary>
public static class CacheReadThrough
{
    public static async Task<Cached<T>> GetOrAddAsync<T>(
        this ICacheService cache, string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
        where T : class
    {
        var cached = await cache.GetAsync<T>(key, cancellationToken);
        if (cached is not null)
        {
            return new Cached<T>(cached, Hit: true);
        }

        var value = await factory(cancellationToken);
        await cache.SetAsync(key, value, ttl, cancellationToken);
        return new Cached<T>(value, Hit: false);
    }
}
