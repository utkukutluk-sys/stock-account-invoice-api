using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAccount.Application.Features.Reconciliation;

namespace StockAccount.Infrastructure.BackgroundServices;

/// <summary>
/// Periodic balance reconciliation (CONTRACT §3.9): first run after <c>InitialDelaySeconds</c> (30 s), then every
/// <c>Reconciliation:IntervalMinutes</c>. Discrepancies are logged at Warning; exceptions never crash the host.
/// </summary>
public sealed class BalanceReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ReconciliationOptions> options,
    ILogger<BalanceReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            logger.LogInformation("Balance reconciliation background service is disabled");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.InitialDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.IntervalMinutes)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutting down
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IReconciliationService>();
            await service.RunAsync(options.CurrentValue.AutoFix, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Balance reconciliation run failed");
        }
    }
}
