using System.Globalization;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Domain.Common;

namespace StockAccount.UnitTests.Application;

/// <summary>
/// Codes, cache keys and hashes must not depend on the current culture: under tr-TR "i".ToUpper() is "İ" and
/// "I".ToLower() is "ı", which would break code uniqueness, ASCII code validation and cache key equality.
/// </summary>
public sealed class TurkishCultureTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    private static T UnderTurkish<T>(Func<T> action)
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
        return action();
    }

    private static T UnderInvariant<T>(Func<T> action)
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        return action();
    }

    [Theory]
    [InlineData("pide-i1", "PIDE-I1")]
    [InlineData(" sut.ilk ", "SUT.ILK")]
    public void Stock_code_normalization_is_culture_invariant(string input, string expected)
    {
        var normalized = UnderTurkish(() => DomainRules.NormalizeCode(input));

        Assert.Equal(expected, normalized);
        Assert.DoesNotContain('İ', normalized);
        Assert.True(UnderTurkish(() => DomainRules.IsValidCode(normalized)));
    }

    [Theory]
    [InlineData("ı1")]
    [InlineData("İ1")]
    public void Non_ascii_turkish_letters_are_rejected_as_codes(string input) =>
        Assert.False(UnderTurkish(() => DomainRules.IsValidCode(DomainRules.NormalizeCode(input))));

    [Fact]
    public void Query_hash_and_keys_are_identical_under_tr_TR_and_invariant()
    {
        static (string Hash, string Key) Compute()
        {
            var query = new StockListQuery { Q = "iİıI", IsActive = true, Page = 2, PageSize = 10 };
            var hash = CacheKeys.QueryHash(query, "-unitPrice,name");
            return (hash, CacheKeys.List(CacheKeys.Stock, "1", "ver", hash));
        }

        var turkish = UnderTurkish(Compute);
        var invariant = UnderInvariant(Compute);

        Assert.Equal(invariant, turkish);
        Assert.Equal("stock:5", UnderTurkish(() => CacheKeys.Entity(CacheKeys.Stock, 5)));
    }
}
