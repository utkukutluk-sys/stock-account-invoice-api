# syntax=docker/dockerfile:1
# Build context: repository root (see docker-compose.yml). Port 8080 (CONTRACT §8.2/§8.3).
# The API migrates and seeds the database on startup when Database__MigrateOnStartup / Database__Seed are true.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached layer): solution-level props + project files only.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/StockAccount.Domain/StockAccount.Domain.csproj src/StockAccount.Domain/
COPY src/StockAccount.Application/StockAccount.Application.csproj src/StockAccount.Application/
COPY src/StockAccount.Infrastructure/StockAccount.Infrastructure.csproj src/StockAccount.Infrastructure/
COPY src/StockAccount.Api/StockAccount.Api.csproj src/StockAccount.Api/
RUN dotnet restore src/StockAccount.Api/StockAccount.Api.csproj

COPY src/ src/
RUN dotnet publish src/StockAccount.Api/StockAccount.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Docker \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app/publish .
# Serilog rolling file sink writes to /app/logs (non-root user "app" from the base image).
RUN mkdir -p /app/logs && chown -R $APP_UID /app/logs
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "StockAccount.Api.dll"]
