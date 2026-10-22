using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StockAccount.Application.Common.Caching;

namespace StockAccount.Infrastructure.Caching;

/// <summary>Which cache provider was selected at startup (logged by the host, used by the health check).</summary>
public sealed class CacheProviderState
{
    public bool Enabled { get; init; }
    public bool UsingRedis { get; init; }
    public IConnectionMultiplexer? Multiplexer { get; init; }
    public string? FallbackReason { get; init; }

    public string Describe() => !Enabled
        ? "disabled (no-op)"
        : UsingRedis ? "Redis" : $"in-memory distributed cache (fallback: {FallbackReason})";
}

public static class CacheRegistration
{
    public const string InstanceName = "sa:";

    /// <summary>
    /// CONTRACT §6: Cache:Enabled=false → no-op; Cache:UseRedis=true and Redis reachable within 2 s → Redis with
    /// instance name "sa:"; otherwise in-memory distributed cache (a Warning is logged by the host at startup).
    /// </summary>
    public static IServiceCollection AddAppCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
        CacheProviderState state;

        if (!options.Enabled)
        {
            state = new CacheProviderState { Enabled = false, FallbackReason = "Cache:Enabled=false" };
            services.AddDistributedMemoryCache();
            services.AddSingleton<ICacheService, NoOpCacheService>();
            services.AddSingleton(state);
            return services;
        }

        state = options.UseRedis
            ? TryConnectRedis(configuration.GetConnectionString("Redis"))
            : new CacheProviderState { Enabled = true, FallbackReason = "Cache:UseRedis=false" };

        if (state is { UsingRedis: true, Multiplexer: { } mux })
        {
            services.AddSingleton(mux);
            var breakDuration = TimeSpan.FromSeconds(Math.Max(1, options.CircuitBreakSeconds));
            services.AddSingleton<IDistributedCache>(sp =>
            {
                var redis = new RedisCache(Options.Create(new RedisCacheOptions
                {
                    InstanceName = InstanceName,
                    ConnectionMultiplexerFactory = () => Task.FromResult(mux),
                }));
                var breaker = new CacheCircuitBreaker(breakDuration, sp.GetService<TimeProvider>() ?? TimeProvider.System);
                return new ResilientDistributedCache(redis, breaker, ct => RecoverAsync(mux, ct), sp.GetRequiredService<ILogger<ResilientDistributedCache>>());
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddSingleton(state);
        services.AddSingleton<ICacheService, DistributedCacheService>();
        return services;
    }


    /// <summary>Half-open probe: ping, then delete every <c>sa:*</c> key so nothing written before the outage is served.</summary>
    internal static async Task RecoverAsync(IConnectionMultiplexer mux, CancellationToken cancellationToken)
    {
        var db = mux.GetDatabase();
        await db.PingAsync();
        foreach (var endpoint in mux.GetEndPoints())
        {
            var server = mux.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            var batch = new List<RedisKey>(256);
            await foreach (var key in server.KeysAsync(pattern: InstanceName + "*", pageSize: 500).WithCancellation(cancellationToken))
            {
                batch.Add(key);
                if (batch.Count == 256)
                {
                    await db.KeyDeleteAsync([.. batch]);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await db.KeyDeleteAsync([.. batch]);
            }
        }
    }
    private static CacheProviderState TryConnectRedis(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new CacheProviderState { Enabled = true, FallbackReason = "ConnectionStrings:Redis is empty" };
        }

        try
        {
            var config = ConfigurationOptions.Parse(connectionString);
            config.AbortOnConnectFail = false;
            // Short timeouts + fail-fast backlog: a dead Redis must not stall requests (BUG-6).
            config.ConnectTimeout = Math.Min(config.ConnectTimeout, 2000);
            config.SyncTimeout = Math.Min(config.SyncTimeout, 1000);
            config.AsyncTimeout = Math.Min(config.AsyncTimeout, 1000);
            config.BacklogPolicy = BacklogPolicy.FailFast;

            var mux = ConnectionMultiplexer.Connect(config);
            if (!mux.IsConnected)
            {
                mux.Dispose();
                return new CacheProviderState { Enabled = true, FallbackReason = $"Redis unreachable at {string.Join(",", config.EndPoints)}" };
            }

            return new CacheProviderState { Enabled = true, UsingRedis = true, Multiplexer = mux };
        }
        catch (Exception ex) when (ex is RedisException or ArgumentException or TimeoutException)
        {
            return new CacheProviderState { Enabled = true, FallbackReason = ex.Message };
        }
    }
}
