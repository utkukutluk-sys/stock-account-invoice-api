using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;

namespace StockAccount.Domain.Entities;

/// <summary>Immutable current-account ledger row (table ActTrans). Never updated or deleted.</summary>
public class ActTrans : ITenantEntity, ICreationAudited
{
    public long Id { get; set; }

    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public int CurrentAccountId { get; set; }
    public CurrentAccount? CurrentAccount { get; set; }

    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public DateOnly TransDate { get; set; }
    public ActTransDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public bool IsReversal { get; set; }
    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }

    /// <summary>Signed amount for balance math: +Amount for Debit, −Amount for Credit.</summary>
    public decimal SignedAmount => Direction == ActTransDirection.Debit ? Amount : -Amount;
}
