using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            services.AddStackExchangeRedisCache(o =>
            {
                o.InstanceName = InstanceName;
                o.ConnectionMultiplexerFactory = () => Task.FromResult(mux);
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
            config.ConnectTimeout = 2000;
            config.SyncTimeout = Math.Min(config.SyncTimeout, 2000);
            config.AsyncTimeout = Math.Min(config.AsyncTimeout, 2000);

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
