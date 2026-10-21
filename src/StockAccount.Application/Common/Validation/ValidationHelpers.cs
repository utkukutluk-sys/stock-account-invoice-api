using FluentValidation;
using FluentValidation.Results;
using StockAccount.Domain.Common;

namespace StockAccount.Application.Common.Validation;

/// <summary>Shared validation helpers and reusable rule fragments.</summary>
public static class ValidationHelpers
{
    /// <summary>Creates a FluentValidation <see cref="ValidationException"/> for one field (→ 400 VALIDATION_FAILED).</summary>
    public static ValidationException Failure(string property, string message) =>
        new(message, [new ValidationFailure(property, message)]);

    /// <summary>Parses the Base64 rowVersion; missing/invalid → 400 with errors["rowVersion"].</summary>
    public static byte[] ParseRowVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            throw Failure("rowVersion", "rowVersion zorunludur.");
        }

        var buffer = new byte[rowVersion.Length];
        if (!Convert.TryFromBase64String(rowVersion, buffer, out var written) || written == 0)
        {
            throw Failure("rowVersion", "rowVersion geçersiz.");
        }

        return buffer[..written];
    }

    public static string? ToBase64(byte[]? rowVersion) => rowVersion is null ? null : Convert.ToBase64String(rowVersion);

    public static bool BeValidTaxNumber(string? value) =>
        value is not null && value.Length is >= 10 and <= 11 && value.All(char.IsAsciiDigit);

    public static bool BeValidCode(string? value) => DomainRules.IsValidCode(DomainRules.NormalizeCode(value));

    public static bool HaveAtMostDecimals(decimal value, int decimals) => decimal.Round(value, decimals) == value;

    /// <summary>Trims and converts blank strings to null.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static IRuleBuilderOptions<T, string?> MaxLen<T>(this IRuleBuilder<T, string?> rule, int max) =>
        rule.MaximumLength(max).WithMessage($"En fazla {max} karakter olabilir.");
}
