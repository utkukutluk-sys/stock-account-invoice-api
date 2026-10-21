using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable("InvoiceLines", t =>
        {
            t.HasCheckConstraint("CK_InvoiceLines_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_InvoiceLines_UnitPrice", "[UnitPrice] >= 0");
            t.HasCheckConstraint("CK_InvoiceLines_VatRate", $"[VatRate] IN {ConfigurationExtensions.VatRatesSql}");
            t.HasCheckConstraint("CK_InvoiceLines_LineNo", "[LineNo] BETWEEN 1 AND 200");
            t.HasCheckConstraint("CK_InvoiceLines_LineTotal", "[LineNet] >= 0 AND [VatAmount] >= 0 AND [LineTotal] = [LineNet] + [VatAmount]");
        });

        b.HasKey(e => e.Id);
        b.Property(e => e.Id).UseIdentityColumn();

        b.Property(e => e.LineNo).IsRequired();
        b.Property(e => e.Description).HasMaxLength(250);
        b.Property(e => e.Quantity).Quantity().IsRequired();
        b.Property(e => e.UnitPrice).Price().IsRequired();
        b.Property(e => e.VatRate).Rate().IsRequired();
        b.Property(e => e.LineNet).Money().IsRequired();
        b.Property(e => e.VatAmount).Money().IsRequired();
        b.Property(e => e.LineTotal).Money().IsRequired();

        b.HasOne(e => e.Stock)
            .WithMany()
            .HasForeignKey(e => e.StockId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(e => new { e.InvoiceId, e.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_InvoiceLines_InvoiceId_LineNo");

        // FK index; also serves the STOCK_IN_USE check.
        b.HasIndex(e => e.StockId).HasDatabaseName("IX_InvoiceLines_StockId");
    }
}
