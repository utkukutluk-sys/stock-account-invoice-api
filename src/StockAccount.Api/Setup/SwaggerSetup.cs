using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using StockAccount.Api.Security;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace StockAccount.Api.Setup;

public static class SwaggerSetup
{
    /// <summary>Swagger UI at /swagger with JWT "Authorize" button, XML comments and an optional X-Company-Id header.</summary>
    public static IServiceCollection AddApiSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "StockAccount API",
                Version = "v1",
                Description = "Stok / Cari / Fatura Web API. Login via POST /api/auth/login, then paste the accessToken into Authorize. "
                    + "Admins may send X-Company-Id to scope reads and creates to one company.",
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT access token (without the 'Bearer ' prefix).",
            });
            // No global requirement: only [Authorize] operations get Bearer (anonymous auth endpoints stay open).
            c.OperationFilter<AuthorizeOperationFilter>();

            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml))
            {
                c.IncludeXmlComments(xml, includeControllerXmlComments: true);
            }

            c.OperationFilter<CompanyHeaderOperationFilter>();
        });

        return services;
    }

    /// <summary>
    /// Bearer requirement + effective policy text for operations with [Authorize] (class or action) and no [AllowAnonymous];
    /// policies of class and action are AND-combined, so all are listed.
    /// </summary>
    private sealed class AuthorizeOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var method = context.MethodInfo;
            if (method.GetCustomAttributes<AllowAnonymousAttribute>(true).Any())
            {
                return;
            }

            var policies = (method.DeclaringType?.GetCustomAttributes<AuthorizeAttribute>(true) ?? [])
                .Concat(method.GetCustomAttributes<AuthorizeAttribute>(true))
                .ToList();
            if (policies.Count == 0)
            {
                return;
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
            });

            var names = policies.Select(p => p.Policy ?? "Authenticated").Distinct().ToList();
            var effective = names.Contains("AdminOnly") ? "AdminOnly" : string.Join(" + ", names);
            operation.Description = $"{operation.Description}{(string.IsNullOrEmpty(operation.Description) ? string.Empty : "\n\n")}**Policy:** {effective}".Trim();
        }
    }

    /// <summary>Adds the optional <c>X-Company-Id</c> header to authorized non-auth operations.</summary>
    private sealed class CompanyHeaderOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var path = context.ApiDescription.RelativePath ?? string.Empty;
            var requiresAuth = context.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(true).Any()
                || context.MethodInfo.DeclaringType?.GetCustomAttributes<AuthorizeAttribute>(true).Any() == true;
            if (!requiresAuth || path.StartsWith("api/auth", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/me", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = HttpCurrentUser.CompanyHeader,
                In = ParameterLocation.Header,
                Required = false,
                Description = "Admin only: company scope (absent = all companies; required for creates).",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            });
        }
    }
}
