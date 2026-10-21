using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Domain.Enums;
using StockAccount.Infrastructure.Caching;

namespace StockAccount.UnitTests.Application;

public class CacheTests
{
    [Fact]
    public void Keys_FollowContractNaming()
    {
        Assert.Equal("stock:5", CacheKeys.Entity(CacheKeys.Stock, 5));
        Assert.Equal("account:ver:all", CacheKeys.Version(CacheKeys.Account, "all"));
        Assert.Equal("stock:list:3:abc:0123456789abcdef", CacheKeys.List(CacheKeys.Stock, "3", "abc", "0123456789abcdef"));
    }

    [Fact]
    public void QueryHash_SameParametersInDifferentOrder_GiveSameHash()
    {
        var a = new StockListQuery { Q = "el", Unit = StockUnit.Kg, Page = 2, PageSize = 10 };
        var b = new StockListQuery { PageSize = 10, Page = 2, Unit = StockUnit.Kg, Q = "el" };

        Assert.Equal(CacheKeys.QueryHash(a, "code"), CacheKeys.QueryHash(b, "code"));
        Assert.Equal(16, CacheKeys.QueryHash(a).Length);
    }

    [Fact]
    public void QueryHash_OmittedSortEqualsExplicitDefault()
    {
        var omitted = new StockListQuery();
        var explicitDefault = new StockListQuery { Sort = "code" };

        Assert.Equal(
            CacheKeys.QueryHash(omitted, omitted.Sort ?? "code"),
            CacheKeys.QueryHash(explicitDefault, explicitDefault.Sort ?? "code"));
    }

    [Theory]
    [InlineData(nameof(StockListQuery.Page))]
    [InlineData(nameof(StockListQuery.Q))]
    [InlineData(nameof(StockListQuery.IsActive))]
    public void QueryHash_DifferentValues_GiveDifferentHash(string changed)
    {
        var a = new StockListQuery();
        var b = new StockListQuery();
        switch (changed)
        {
            case nameof(StockListQuery.Page): b.Page = 2; break;
            case nameof(StockListQuery.Q): b.Q = "x"; break;
            default: b.IsActive = true; break;
        }

        Assert.NotEqual(CacheKeys.QueryHash(a, "code"), CacheKeys.QueryHash(b, "code"));
    }

    [Fact]
    public async Task Invalidator_StockChanged_RemovesEntity_AndBumpsCompanyAndAllVersions()
    {
        var cache = Substitute.For<ICacheService>();
        var invalidator = new CacheInvalidator(cache);

        await invalidator.StockChangedAsync(7, [1, 2, 2]);

        await cache.Received(1).RemoveAsync("stock:1", Arg.Any<CancellationToken>());
        await cache.Received(1).RemoveAsync("stock:2", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("stock", "7", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("stock", "all", Arg.Any<CancellationToken>());
        await cache.DidNotReceive().BumpVersionAsync("account", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidator_InvoicePosted_TouchesStocksAndAccount()
    {
        var cache = Substitute.For<ICacheService>();

        await new CacheInvalidator(cache).InvoicePostedAsync(7, [1], 3);

        await cache.Received(1).RemoveAsync("stock:1", Arg.Any<CancellationToken>());
        await cache.Received(1).RemoveAsync("account:3", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("account", "7", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("account", "all", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidator_CompanyChanged_BumpsBothEntities()
    {
        var cache = Substitute.For<ICacheService>();

        await new CacheInvalidator(cache).CompanyChangedAsync(4);

        await cache.Received(1).BumpVersionAsync("stock", "4", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("account", "4", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("stock", "all", Arg.Any<CancellationToken>());
        await cache.Received(1).BumpVersionAsync("account", "all", Arg.Any<CancellationToken>());
        await cache.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheService_RoundTripsDtos_AndVersionsChangeOnBump()
    {
        var service = new DistributedCacheService(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<DistributedCacheService>.Instance);
        var page = new PagedResult<StockDto>(
            [new StockDto(1, 7, "Demo", "ELMA", "Elma", StockUnit.Kg, null, 1m, 50m, null, true, 98m, DateTime.UtcNow, null, "AAAAAAAAB9E=")],
            1, 20, 1);

        await service.SetAsync("stock:list:x", page, TimeSpan.FromMinutes(1));
        var read = await service.GetAsync<PagedResult<StockDto>>("stock:list:x");

        Assert.NotNull(read);
        Assert.Equal(98m, read.Items[0].Balance);
        Assert.Equal(StockUnit.Kg, read.Items[0].Unit);
        Assert.Equal(1, read.TotalPages);

        var v1 = await service.GetVersionAsync("stock", "7");
        Assert.Equal(v1, await service.GetVersionAsync("stock", "7"));
        await service.BumpVersionAsync("stock", "7");
        Assert.NotEqual(v1, await service.GetVersionAsync("stock", "7"));
    }

    [Fact]
    public async Task CacheService_SwallowsCacheFailures()
    {
        var broken = Substitute.For<IDistributedCache>();
        broken.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("redis down"));
        broken.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));
        broken.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("redis down"));
        var service = new DistributedCacheService(broken, NullLogger<DistributedCacheService>.Instance);

        Assert.Null(await service.GetAsync<StockDto>("stock:1"));
        await service.SetAsync("stock:1", new object(), TimeSpan.FromMinutes(1));
        await service.RemoveAsync("stock:1");
        Assert.Equal(DistributedCacheService.UnavailableVersion, await service.GetVersionAsync("stock", "1"));
        await service.BumpVersionAsync("stock", "1");
    }

    [Fact]
    public async Task ReadThrough_MissThenHit()
    {
        var service = new DistributedCacheService(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<DistributedCacheService>.Instance);
        var calls = 0;

        var first = await service.GetOrAddAsync("k", TimeSpan.FromMinutes(1), _ => { calls++; return Task.FromResult("v"); }, CancellationToken.None);
        var second = await service.GetOrAddAsync("k", TimeSpan.FromMinutes(1), _ => { calls++; return Task.FromResult("v"); }, CancellationToken.None);

        Assert.False(first.Hit);
        Assert.True(second.Hit);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoOpCache_AlwaysMisses()
    {
        var service = new NoOpCacheService();
        await service.SetAsync("k", "v", TimeSpan.FromMinutes(1));

        Assert.Null(await service.GetAsync<string>("k"));
    }
}
