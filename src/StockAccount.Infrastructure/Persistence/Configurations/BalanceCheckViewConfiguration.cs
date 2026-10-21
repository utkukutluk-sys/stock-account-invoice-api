using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Infrastructure.Persistence.Views;

namespace StockAccount.Infrastructure.Persistence.Configurations;

/// <summary>Keyless read-only mappings of the reconciliation views (created by SQL in the InitialCreate migration).</summary>
internal sealed class StockBalanceCheckConfiguration : IEntityTypeConfiguration<StockBalanceCheck>
{
    public const string ViewName = "vw_StockBalanceReconciliation";

    public void Configure(EntityTypeBuilder<StockBalanceCheck> b)
    {
        b.HasNoKey();
        b.ToView(ViewName);
        b.Property(e => e.StoredBalance).Quantity();
        b.Property(e => e.LedgerBalance).Quantity();
        b.Property(e => e.Difference).Quantity();
    }
}

internal sealed class AccountBalanceCheckConfiguration : IEntityTypeConfiguration<AccountBalanceCheck>
{
    public const string ViewName = "vw_AccountBalanceReconciliation";

    public void Configure(EntityTypeBuilder<AccountBalanceCheck> b)
    {
        b.HasNoKey();
        b.ToView(ViewName);
        b.Property(e => e.StoredBalance).Money();
        b.Property(e => e.LedgerBalance).Money();
        b.Property(e => e.Difference).Money();
    }
}
