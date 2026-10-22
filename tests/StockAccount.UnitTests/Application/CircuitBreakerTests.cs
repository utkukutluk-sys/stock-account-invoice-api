using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StockAccount.Infrastructure.Caching;

namespace StockAccount.UnitTests.Application;

/// <summary>BUG-6: Redis failures open a circuit; Redis is bypassed until the half-open probe (ping + flush) succeeds.</summary>
public class CircuitBreakerTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DistributedCacheEntryOptions Ttl = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

    private readonly ManualTime _time = new();
    private readonly IDistributedCache _redis = Substitute.For<IDistributedCache>();
    private int _recoveries;
    private bool _recoveryFails = true;

    private ResilientDistributedCache Create(out CacheCircuitBreaker breaker)
    {
        breaker = new CacheCircuitBreaker(TimeSpan.FromSeconds(30), _time);
        return new ResilientDistributedCache(
            _redis,
            breaker,
            _ =>
            {
                _recoveries++;
                return _recoveryFails ? throw new InvalidOperationException("still down") : Task.CompletedTask;
            },
            NullLogger<ResilientDistributedCache>.Instance);
    }

    [Fact]
    public async Task FirstFailure_OpensCircuit_AndLaterCallsSkipRedis()
    {
        _redis.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("redis down"));
        var cache = Create(out var breaker);

        Assert.Null(await cache.GetAsync("sa:stock:1"));
        Assert.True(breaker.IsOpen);

        await cache.SetAsync("sa:stock:1", [1, 2, 3], Ttl);
        Assert.Equal(new byte[] { 1, 2, 3 }, await cache.GetAsync("sa:stock:1")); // served by the in-memory fallback
        await cache.RemoveAsync("sa:stock:1");
        Assert.Null(await cache.GetAsync("sa:stock:1"));

        await _redis.Received(1).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _redis.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
        await _redis.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, _recoveries); // no probe before the break elapsed
    }

    [Fact]
    public async Task AfterBreak_FailedProbe_KeepsCircuitOpen_SuccessfulProbe_FlushesAndCloses()
    {
        _redis.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("redis down"));
        var cache = Create(out var breaker);
        await cache.GetAsync("k");

        _time.Now += TimeSpan.FromSeconds(31);
        await cache.GetAsync("k");
        Assert.Equal(1, _recoveries);
        Assert.True(breaker.IsOpen);

        await cache.GetAsync("k"); // re-opened for another 30 s: no new probe
        Assert.Equal(1, _recoveries);

        _time.Now += TimeSpan.FromSeconds(31);
        _recoveryFails = false;
        _redis.GetAsync("k", Arg.Any<CancellationToken>()).Returns(new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, await cache.GetAsync("k"));
        Assert.Equal(2, _recoveries);
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public async Task NewOutage_StartsWithEmptyFallback()
    {
        _redis.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("redis down"));
        var cache = Create(out _);
        await cache.GetAsync("k");
        await cache.SetAsync("k", [1], Ttl); // written to the fallback during outage 1

        _recoveryFails = false;
        _time.Now += TimeSpan.FromSeconds(31);
        await cache.GetAsync("k"); // probe succeeds → closed, Redis call throws again → outage 2 with a fresh fallback

        Assert.Null(await cache.GetAsync("k"));
    }

    [Fact]
    public void Breaker_AllowsOnlyOneProbe()
    {
        var breaker = new CacheCircuitBreaker(TimeSpan.FromSeconds(5), _time);
        Assert.True(breaker.Trip());
        Assert.False(breaker.Trip());
        Assert.False(breaker.TryBeginProbe());

        _time.Now += TimeSpan.FromSeconds(6);
        Assert.True(breaker.TryBeginProbe());
        Assert.False(breaker.TryBeginProbe());
        breaker.Close();
        Assert.False(breaker.IsOpen);
    }
}
