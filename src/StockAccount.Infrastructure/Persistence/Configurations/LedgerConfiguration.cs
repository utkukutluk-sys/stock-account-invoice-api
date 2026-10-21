using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Configurations;

/// <summary>
/// Ledger tables are append-only. Besides the interceptor guard, the InitialCreate migration installs
/// INSTEAD OF UPDATE, DELETE triggers; they are declared here so EF does not use OUTPUT without INTO on these tables.
/// </summary>
internal sealed class StockTransConfiguration : IEntityTypeConfiguration<StockTrans>
{
    public const string ImmutableTrigger = "TR_StockTrans_Immutable";

    public void Configure(EntityTypeBuilder<StockTrans> b)
    {
        b.ToTable("StockTrans", t =>
        {
            t.HasTrigger(ImmutableTrigger);
            t.HasCheckConstraint("CK_StockTrans_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_StockTrans_Direction", "[Direction] IN (1, 2)");
            t.HasCheckConstraint("CK_StockTrans_Amount", "[Amount] >= 0");
        });

        b.HasKey(e => e.Id);
        b.Property(e => e.Id).UseIdentityColumn();

        b.Property(e => e.TransDate).HasColumnType("date").IsRequired();
        b.Property(e => e.Direction).IsRequired();
        b.Property(e => e.Quantity).Quantity().IsRequired();
        b.Property(e => e.UnitPrice).Price().IsRequired();
        b.Property(e => e.Amount).Money().IsRequired();
        b.Property(e => e.IsReversal).IsRequired();
        b.Property(e => e.Description).HasMaxLength(250).IsRequired();
        b.Property(e => e.CreatedAt).IsRequired();

        b.HasOne(e => e.Company).WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Stock).WithMany().HasForeignKey(e => e.StockId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Invoice).WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        // Covers per-stock history paging AND the balance aggregation (SUM by StockId) without key lookups.
        b.HasIndex(e => new { e.StockId, e.TransDate })
            .IncludeProperties(e => new { e.Direction, e.Quantity })
            .HasDatabaseName("IX_StockTrans_StockId_TransDate");
        b.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_StockTrans_InvoiceId");
        b.HasIndex(e => new { e.CompanyId, e.TransDate }).HasDatabaseName("IX_StockTrans_CompanyId_TransDate");
    }
}

internal sealed class ActTransConfiguration : IEntityTypeConfiguration<ActTrans>
{
    public const string ImmutableTrigger = "TR_ActTrans_Immutable";

    public void Configure(EntityTypeBuilder<ActTrans> b)
    {
        b.ToTable("ActTrans", t =>
        {
            t.HasTrigger(ImmutableTrigger);
            t.HasCheckConstraint("CK_ActTrans_Amount", "[Amount] >= 0");
            t.HasCheckConstraint("CK_ActTrans_Direction", "[Direction] IN (1, 2)");
        });

        b.HasKey(e => e.Id);
        b.Property(e => e.Id).UseIdentityColumn();

        b.Property(e => e.TransDate).HasColumnType("date").IsRequired();
        b.Property(e => e.Direction).IsRequired();
        b.Property(e => e.Amount).Money().IsRequired();
        b.Property(e => e.IsReversal).IsRequired();
        b.Property(e => e.Description).HasMaxLength(250).IsRequired();
        b.Property(e => e.CreatedAt).IsRequired();

        b.HasOne(e => e.Company).WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.CurrentAccount).WithMany().HasForeignKey(e => e.CurrentAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Invoice).WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(e => new { e.CurrentAccountId, e.TransDate })
            .IncludeProperties(e => new { e.Direction, e.Amount })
            .HasDatabaseName("IX_ActTrans_CurrentAccountId_TransDate");
        b.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_ActTrans_InvoiceId");
        b.HasIndex(e => new { e.CompanyId, e.TransDate }).HasDatabaseName("IX_ActTrans_CompanyId_TransDate");
    }
}
