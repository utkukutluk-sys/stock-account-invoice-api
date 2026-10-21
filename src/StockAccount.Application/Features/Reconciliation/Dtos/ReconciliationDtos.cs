namespace StockAccount.Application.Features.Reconciliation.Dtos;

public sealed record ReconciliationDiscrepancyDto(
    string EntityType,
    int EntityId,
    int CompanyId,
    string Code,
    decimal StoredBalance,
    decimal LedgerBalance,
    decimal Difference);

public sealed record ReconciliationResultDto(
    DateTime CheckedAt,
    int StocksChecked,
    int AccountsChecked,
    bool Fixed,
    IReadOnlyList<ReconciliationDiscrepancyDto> Discrepancies);
