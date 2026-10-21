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
            c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            });

            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml))
            {
                c.IncludeXmlComments(xml, includeControllerXmlComments: true);
            }

            c.OperationFilter<CompanyHeaderOperationFilter>();
        });

        return services;
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
