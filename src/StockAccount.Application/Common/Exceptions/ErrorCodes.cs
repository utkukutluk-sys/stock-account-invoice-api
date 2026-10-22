namespace StockAccount.Application.Common.Exceptions;

/// <summary>Error codes returned in ProblemDetails.code (CONTRACT §5.2) and their HTTP status.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string CompanyRequired = "COMPANY_REQUIRED";

    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string UserInactive = "USER_INACTIVE";
    public const string InvalidRefreshToken = "INVALID_REFRESH_TOKEN";

    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";

    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string DuplicateCode = "DUPLICATE_CODE";
    public const string DuplicateEmail = "DUPLICATE_EMAIL";
    public const string DuplicateTaxNumber = "DUPLICATE_TAX_NUMBER";
    public const string InvoiceNotDraft = "INVOICE_NOT_DRAFT";
    public const string InvoiceNotApproved = "INVOICE_NOT_APPROVED";
    public const string StockHasBalance = "STOCK_HAS_BALANCE";
    public const string StockInUse = "STOCK_IN_USE";
    public const string AccountHasBalance = "ACCOUNT_HAS_BALANCE";
    public const string AccountInUse = "ACCOUNT_IN_USE";
    public const string CompanyNotEmpty = "COMPANY_NOT_EMPTY";
    public const string CannotDeleteSelf = "CANNOT_DELETE_SELF";

    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string InvoiceHasNoLines = "INVOICE_HAS_NO_LINES";
    public const string InactiveStock = "INACTIVE_STOCK";
    public const string InactiveAccount = "INACTIVE_ACCOUNT";
    public const string AccountTypeMismatch = "ACCOUNT_TYPE_MISMATCH";
    public const string InvalidReference = "INVALID_REFERENCE";

    public const string RateLimited = "RATE_LIMITED";

    public const string InternalError = "INTERNAL_ERROR";

    private static readonly Dictionary<string, int> StatusByCode = new(StringComparer.Ordinal)
    {
        [ValidationFailed] = 400, [CompanyRequired] = 400,
        [Unauthorized] = 401, [InvalidCredentials] = 401, [AccountLocked] = 401, [UserInactive] = 401, [InvalidRefreshToken] = 401,
        [Forbidden] = 403,
        [NotFound] = 404,
        [ConcurrencyConflict] = 409, [DuplicateCode] = 409, [DuplicateEmail] = 409, [DuplicateTaxNumber] = 409,
        [InvoiceNotDraft] = 409, [InvoiceNotApproved] = 409, [StockHasBalance] = 409, [StockInUse] = 409,
        [AccountHasBalance] = 409, [AccountInUse] = 409, [CompanyNotEmpty] = 409, [CannotDeleteSelf] = 409,
        [InsufficientStock] = 422, [InvoiceHasNoLines] = 422, [InactiveStock] = 422, [InactiveAccount] = 422,
        [AccountTypeMismatch] = 422, [InvalidReference] = 422,
        [RateLimited] = 429,
        [InternalError] = 500,
    };

    /// <summary>HTTP status for a code; unknown codes are treated as business rule violations (422).</summary>
    public static int StatusFor(string code) => StatusByCode.TryGetValue(code, out var status) ? status : 422;
}
