using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Domain.Common;

namespace StockAccount.Infrastructure.Persistence.Configurations;

/// <summary>Shared column conventions for the contract's base types (CONTRACT §2.1).</summary>
internal static class ConfigurationExtensions
{
    public const int MoneyPrecision = 18, MoneyScale = 2;
    public const int PricePrecision = 18, PriceScale = 4;
    public const int QuantityPrecision = 18, QuantityScale = 3;
    public const int RatePrecision = 5, RateScale = 2;

    /// <summary>SQL list of allowed VAT rates for CHECK constraints.</summary>
    public const string VatRatesSql = "(0, 1, 10, 20)";

    public static void ConfigureAuditable<T>(this EntityTypeBuilder<T> b)
        where T : AuditableEntity
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).UseIdentityColumn();
        b.Property(e => e.CreatedAt).IsRequired();
    }

    public static void ConfigureSoftDelete<T>(this EntityTypeBuilder<T> b)
        where T : class, ISoftDelete
    {
        b.Property(e => e.IsDeleted).IsRequired();
        b.HasQueryFilter(QueryFilterNames.SoftDelete, e => !e.IsDeleted);
    }

    public static void ConfigureRowVersion<T>(this EntityTypeBuilder<T> b)
        where T : class, IHasRowVersion
    {
        b.Property(e => e.RowVersion).IsRowVersion();
    }

    public static PropertyBuilder<decimal> Money(this PropertyBuilder<decimal> p) => p.HasPrecision(MoneyPrecision, MoneyScale);
    public static PropertyBuilder<decimal> Price(this PropertyBuilder<decimal> p) => p.HasPrecision(PricePrecision, PriceScale);
    public static PropertyBuilder<decimal> Quantity(this PropertyBuilder<decimal> p) => p.HasPrecision(QuantityPrecision, QuantityScale);
    public static PropertyBuilder<decimal> Rate(this PropertyBuilder<decimal> p) => p.HasPrecision(RatePrecision, RateScale);

    /// <summary>varchar(n) (non-unicode) column; works for string and string?.</summary>
    public static PropertyBuilder<T> Varchar<T>(this PropertyBuilder<T> p, int length) => p.HasMaxLength(length).IsUnicode(false);

    /// <summary>CHECK: 10–11 ASCII digits.</summary>
    public static string TaxNumberCheckSql(string column, bool nullable) =>
        (nullable ? $"[{column}] IS NULL OR " : string.Empty)
        + $"(LEN([{column}]) BETWEEN 10 AND 11 AND [{column}] NOT LIKE '%[^0-9]%')";
}
