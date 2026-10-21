using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StockAccount.Application.Common.Caching;

namespace StockAccount.Infrastructure.Caching;

/// <summary>
/// <see cref="ICacheService"/> over <see cref="IDistributedCache"/> (Redis or in-memory fallback) with System.Text.Json.
/// Every cache error is caught and logged at Warning; reads then behave as misses (CONTRACT §6).
/// </summary>
public sealed class DistributedCacheService(IDistributedCache cache, ILogger<DistributedCacheService> logger) : ICacheService
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Version token used when the cache cannot be read (list keys then simply miss).</summary>
    public const string UnavailableVersion = "unavailable";

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            var bytes = await cache.GetAsync(key, cancellationToken);
            return bytes is null ? null : JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache read failed for {CacheKey}", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache write failed for {CacheKey}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache remove failed for {CacheKey}", key);
        }
    }

    public async Task<string> GetVersionAsync(string entity, string scope, CancellationToken cancellationToken = default)
    {
        var key = CacheKeys.Version(entity, scope);
        try
        {
            var current = await cache.GetStringAsync(key, cancellationToken);
            if (!string.IsNullOrEmpty(current))
            {
                return current;
            }

            var token = NewToken();
            await cache.SetStringAsync(key, token, VersionEntryOptions(), cancellationToken);
            return token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache version read failed for {CacheKey}", key);
            return UnavailableVersion;
        }
    }

    public async Task BumpVersionAsync(string entity, string scope, CancellationToken cancellationToken = default)
    {
        var key = CacheKeys.Version(entity, scope);
        try
        {
            await cache.SetStringAsync(key, NewToken(), VersionEntryOptions(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache version bump failed for {CacheKey}", key);
        }
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");

    private static DistributedCacheEntryOptions VersionEntryOptions() => new() { SlidingExpiration = CacheKeys.VersionSlidingTtl };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary><c>Cache:Enabled = false</c>: every read is a miss, writes are ignored.</summary>
public sealed class NoOpCacheService : ICacheService
{
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class => Task.FromResult<T?>(null);

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
        where T : class => Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> GetVersionAsync(string entity, string scope, CancellationToken cancellationToken = default) => Task.FromResult("0");

    public Task BumpVersionAsync(string entity, string scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
