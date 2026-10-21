using System.Globalization;
using System.Security.Claims;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Common.Validation;

namespace StockAccount.Api.Security;

/// <summary>
/// <see cref="ICurrentUser"/> from the JWT principal (CONTRACT §4.3). For admins, <c>X-Company-Id</c> selects the company
/// (absent = all companies); for other users the header is ignored and the <c>company_id</c> claim is used.
/// Outside a request (seeding, background services) the user is anonymous and unscoped.
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string CompanyHeader = "X-Company-Id";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public int? UserId => ParseInt(Principal?.FindFirstValue(AppClaimTypes.Subject));

    public string? Email => Principal?.FindFirstValue(AppClaimTypes.Email);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public bool IsAdmin => IsAuthenticated && Principal!.IsInRole(Roles.Admin);

    public int? CompanyId
    {
        get
        {
            if (!IsAuthenticated)
            {
                return null;
            }

            if (!IsAdmin)
            {
                return ParseInt(Principal!.FindFirstValue(AppClaimTypes.CompanyId));
            }

            var header = accessor.HttpContext!.Request.Headers[CompanyHeader].ToString();
            if (string.IsNullOrWhiteSpace(header))
            {
                return null;
            }

            return ParseInt(header) is int id && id > 0
                ? id
                : throw ValidationHelpers.Failure(CompanyHeader, "X-Company-Id geçerli bir firma numarası olmalıdır.");
        }
    }

    public int? CurrentAccountId => ParseInt(Principal?.FindFirstValue(AppClaimTypes.AccountId));

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
