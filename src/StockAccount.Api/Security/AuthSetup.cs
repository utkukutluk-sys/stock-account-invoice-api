using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StockAccount.Api.ErrorHandling;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Security;
using StockAccount.Infrastructure.Identity;

namespace StockAccount.Api.Security;

public static class AuthSetup
{
    /// <summary>JWT bearer (only API auth scheme) + policies AdminOnly / Operator / Portal / Authenticated (CONTRACT §4).</summary>
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var signingKey = JwtTokenService.CreateSigningKey(jwt.Key);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AppClaimTypes.Name,
                    RoleClaimType = AppClaimTypes.Role,
                };
                o.Events = new JwtBearerEvents
                {
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await ProblemWriter.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "Oturum açmanız gerekiyor.");
                    },
                    OnForbidden = ctx =>
                        ProblemWriter.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Bu işlem için yetkiniz yok."),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.AdminOnly, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(Policies.Operator, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                c.User.IsInRole(Roles.Admin)
                || (c.User.IsInRole(Roles.User) && !c.User.HasClaim(cl => cl.Type == AppClaimTypes.AccountId))))
            .AddPolicy(Policies.Portal, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                c.User.IsInRole(Roles.User) && c.User.HasClaim(cl => cl.Type == AppClaimTypes.AccountId)));

        services.AddHttpContextAccessor();
        return services;
    }
}
