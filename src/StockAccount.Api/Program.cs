using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Identity;
using StockAccount.Infrastructure.Persistence;
using StockAccount.Infrastructure.Persistence.Seed;

// MINIMAL BOOTSTRAP created by the DB agent so the solution builds and the database can be migrated/seeded.
// Owned by the Backend agent from here on (BE-2, BE-8, BE-9): replace freely.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPersistence(builder.Configuration);

// Placeholder Identity registration (Backend replaces with full config, CONTRACT §4.1).
builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireDigit = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<AppRole>()
    .AddEntityFrameworkStores<AppDbContext>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

if (app.Configuration.GetValue("Database:Seed", false))
{
    await DataSeeder.SeedAsync(app.Services);
}

app.MapGet("/", () => "StockAccount API");

await app.RunAsync();

public partial class Program;
