using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StockAccount.Infrastructure.Caching;

/// <summary>
/// Circuit breaker state for the Redis cache (BUG-6). Closed → Redis is used. The first Redis failure opens it for
/// <c>breakDuration</c>; meanwhile nothing touches Redis. After that, exactly one caller may run the half-open probe.
/// </summary>
public sealed class CacheCircuitBreaker(TimeSpan breakDuration, TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private DateTimeOffset _openUntil;
    private bool _open;
    private bool _probing;

    public TimeSpan BreakDuration => breakDuration;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>Opens the breaker. Returns true when this call changed the state from closed to open.</summary>
    public bool Trip()
    {
        lock (_gate)
        {
            var wasClosed = !_open;
            _open = true;
            _probing = false;
            _openUntil = timeProvider.GetUtcNow() + breakDuration;
            return wasClosed;
        }
    }

    /// <summary>True for the single caller allowed to probe after the break elapsed (half-open).</summary>
    public bool TryBeginProbe()
    {
        lock (_gate)
        {
            if (!_open || _probing || timeProvider.GetUtcNow() < _openUntil)
            {
                return false;
            }

            _probing = true;
            return true;
        }
    }

    /// <summary>Probe succeeded: close.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _open = false;
            _probing = false;
        }
    }
}

/// <summary>
/// <see cref="IDistributedCache"/> decorator for Redis (BUG-6): Redis errors trip a <see cref="CacheCircuitBreaker"/>;
/// while it is open all operations go to a process-local in-memory cache (fresh instance per outage, so nothing from an
/// earlier outage is served) and Redis is not contacted, so requests do not wait for Redis timeouts. The half-open probe
/// runs <c>recovery</c> (ping + delete every <c>sa:*</c> key, because invalidations during the outage only reached the
/// local cache and Redis may still hold — or reload from its dump — entries written before the outage); only after that
/// succeeds is Redis used again.
/// </summary>
public sealed class ResilientDistributedCache(
    IDistributedCache primary,
    CacheCircuitBreaker breaker,
    Func<CancellationToken, Task> recovery,
    ILogger<ResilientDistributedCache> logger) : IDistributedCache
{
    private volatile IDistributedCache _fallback = NewFallback();

    public byte[]? Get(string key) => Run(c => c.Get(key));

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => RunAsync(c => c.GetAsync(key, token), token);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Run(c => { c.Set(key, value, options); return true; });

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        RunAsync(async c => { await c.SetAsync(key, value, options, token); return true; }, token);

    public void Refresh(string key) => Run(c => { c.Refresh(key); return true; });

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        RunAsync(async c => { await c.RefreshAsync(key, token); return true; }, token);

    public void Remove(string key) => Run(c => { c.Remove(key); return true; });

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        RunAsync(async c => { await c.RemoveAsync(key, token); return true; }, token);

    private T Run<T>(Func<IDistributedCache, T> operation)
    {
        // Synchronous callers never probe; they use Redis only while the breaker is closed.
        if (breaker.IsOpen)
        {
            return operation(_fallback);
        }

        try
        {
            return operation(primary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnPrimaryFailure(ex);
            return operation(_fallback);
        }
    }

    private async Task<T> RunAsync<T>(Func<IDistributedCache, Task<T>> operation, CancellationToken token)
    {
        if (breaker.IsOpen && !await TryRecoverAsync(token))
        {
            return await operation(_fallback);
        }

        try
        {
            return await operation(primary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnPrimaryFailure(ex);
            return await operation(_fallback);
        }
    }

    private async Task<bool> TryRecoverAsync(CancellationToken token)
    {
        if (!breaker.TryBeginProbe())
        {
            return false;
        }

        try
        {
            await recovery(token);
            breaker.Close();
            logger.LogInformation("Redis cache recovered; stale entries flushed, switching back from the in-memory fallback");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            breaker.Trip();
            logger.LogWarning("Redis cache still unavailable ({Error}); in-memory fallback for another {Seconds} s", ex.Message, breaker.BreakDuration.TotalSeconds);
            return false;
        }
    }

    private void OnPrimaryFailure(Exception ex)
    {
        if (breaker.Trip())
        {
            _fallback = NewFallback();
            logger.LogWarning(ex, "Redis cache failed; circuit open for {Seconds} s, using the in-memory fallback", breaker.BreakDuration.TotalSeconds);
        }
    }

    private static MemoryDistributedCache NewFallback() => new(Options.Create(new MemoryDistributedCacheOptions()));
}
