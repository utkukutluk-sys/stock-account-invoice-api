using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;

namespace StockAccount.Domain.Entities;

/// <summary>Current account / Cari (table CurrentAccounts).</summary>
public class CurrentAccount : AuditableEntity, ISoftDelete, ITenantEntity, IHasRowVersion
{
    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public CurrentAccountType Type { get; set; }
    public string? TaxNumber { get; set; }
    public string? TaxOffice { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>System-maintained: Σ Debit − Σ Credit over ActTrans. Positive = receivable (Borçlu).</summary>
    public decimal Balance { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Sales needs Customer|Both, Purchase needs Supplier|Both (ACCOUNT_TYPE_MISMATCH otherwise).</summary>
    public bool IsCompatibleWith(InvoiceType invoiceType) => invoiceType switch
    {
        InvoiceType.Sales => Type is CurrentAccountType.Customer or CurrentAccountType.Both,
        InvoiceType.Purchase => Type is CurrentAccountType.Supplier or CurrentAccountType.Both,
        _ => false,
    };
}
