using System.Text.RegularExpressions;

namespace StockAccount.Domain.Common;

/// <summary>Shared domain constants and helpers (rounding, VAT rates, code normalization).</summary>
public static partial class DomainRules
{
    /// <summary>Allowed VAT rates (percent).</summary>
    public static readonly IReadOnlyList<decimal> AllowedVatRates = [0m, 1m, 10m, 20m];

    public const int MaxInvoiceLines = 200;
    public const string DefaultSalesSeries = "SAT";
    public const string DefaultPurchaseSeries = "ALS";

    /// <summary>Description of the seeded opening purchase invoice that Backend approves at startup (CONTRACT §9).</summary>
    public const string SeedOpeningInvoiceDescription = "Açılış alımı (seed)";

    public static bool IsAllowedVatRate(decimal rate) => AllowedVatRates.Contains(rate);

    /// <summary>Contract rounding: 2 decimals, midpoint away from zero.</summary>
    public static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Trim + ToUpperInvariant (Stock.Code, CurrentAccount.Code, Invoice.Series).</summary>
    public static string NormalizeCode(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    public static bool IsValidCode(string? normalizedCode) =>
        normalizedCode is not null && CodeRegex().IsMatch(normalizedCode);

    public static bool IsValidSeries(string? normalizedSeries) =>
        normalizedSeries is not null && SeriesRegex().IsMatch(normalizedSeries);

    [GeneratedRegex("^[A-Z0-9._-]{1,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^[A-Z0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeriesRegex();
}
