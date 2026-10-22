using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StockAccount.Infrastructure.Startup;

/// <summary>
/// Runs <see cref="DatabaseInitializer"/> (schema guard, migrate, seed, opening-invoice approval) in
/// <see cref="IHostedLifecycleService.StartingAsync"/>, i.e. before Kestrel starts listening and before other hosted
/// services start. Any failure (e.g. <see cref="DatabaseSchemaMismatchException"/>) aborts host start.
/// </summary>
public sealed class DatabaseStartupService(IServiceProvider services, IConfiguration configuration, ILogger<DatabaseStartupService> logger)
    : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) =>
        DatabaseInitializer.InitializeAsync(services, configuration, logger, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
