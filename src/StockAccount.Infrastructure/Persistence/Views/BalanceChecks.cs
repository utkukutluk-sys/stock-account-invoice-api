namespace StockAccount.Infrastructure.Persistence.Views;

/// <summary>Row of view <c>dbo.vw_StockBalanceReconciliation</c>. Difference = StoredBalance − LedgerBalance.</summary>
public class StockBalanceCheck
{
    public int StockId { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public decimal StoredBalance { get; set; }
    public decimal LedgerBalance { get; set; }
    public decimal Difference { get; set; }
}

/// <summary>Row of view <c>dbo.vw_AccountBalanceReconciliation</c>. Difference = StoredBalance − LedgerBalance.</summary>
public class AccountBalanceCheck
{
    public int CurrentAccountId { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public decimal StoredBalance { get; set; }
    public decimal LedgerBalance { get; set; }
    public decimal Difference { get; set; }
}
