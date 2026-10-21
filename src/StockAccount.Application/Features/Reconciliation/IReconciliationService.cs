using StockAccount.Application.Features.Reconciliation.Dtos;

namespace StockAccount.Application.Features.Reconciliation;

/// <summary>Stored balance vs ledger check for all companies (CONTRACT §3.9). Implemented in Infrastructure (DB views).</summary>
public interface IReconciliationService
{
    /// <param name="fix">When true, sets stored balance = ledger balance for every discrepancy.</param>
    Task<ReconciliationResultDto> RunAsync(bool fix, CancellationToken cancellationToken = default);
}

/// <summary>Configuration section "Reconciliation".</summary>
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
    public int InitialDelaySeconds { get; set; } = 30;
    public bool AutoFix { get; set; }
}
