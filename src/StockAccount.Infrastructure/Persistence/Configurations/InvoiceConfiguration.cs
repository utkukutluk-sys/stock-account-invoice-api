using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("Invoices", t =>
        {
            t.HasCheckConstraint("CK_Invoices_Type", "[Type] IN (1, 2)");
            t.HasCheckConstraint("CK_Invoices_Status", "[Status] IN (1, 2, 3)");
            t.HasCheckConstraint("CK_Invoices_DueDate", "[DueDate] IS NULL OR [DueDate] >= [InvoiceDate]");
            t.HasCheckConstraint("CK_Invoices_Number", "[Number] IS NULL OR [Number] > 0");

            // Draft ⇔ not numbered; Approved/Cancelled ⇔ numbered (numbers are only assigned at approval).
            t.HasCheckConstraint(
                "CK_Invoices_Numbering",
                "([Status] = 1 AND [Number] IS NULL AND [DocumentNo] IS NULL) OR ([Status] <> 1 AND [Number] IS NOT NULL AND [DocumentNo] IS NOT NULL)");
            t.HasCheckConstraint("CK_Invoices_Approval", "[Status] = 1 OR [ApprovedAt] IS NOT NULL");
            t.HasCheckConstraint("CK_Invoices_Cancellation", "[Status] <> 3 OR ([CancelledAt] IS NOT NULL AND [CancelReason] IS NOT NULL)");
            t.HasCheckConstraint("CK_Invoices_Totals", "[SubTotal] >= 0 AND [VatTotal] >= 0 AND [GrandTotal] = [SubTotal] + [VatTotal]");
        });

        b.ConfigureAuditable();
        b.ConfigureRowVersion();

        b.Property(e => e.Type).IsRequired();
        b.Property(e => e.Status).IsRequired();
        b.Property(e => e.Series).Varchar(10).IsRequired();
        b.Property(e => e.DocumentNo).Varchar(20);
        b.Property(e => e.InvoiceDate).HasColumnType("date").IsRequired();
        b.Property(e => e.DueDate).HasColumnType("date");
        b.Property(e => e.Description).HasMaxLength(500);
        b.Property(e => e.SubTotal).Money().IsRequired();
        b.Property(e => e.VatTotal).Money().IsRequired();
        b.Property(e => e.GrandTotal).Money().IsRequired();
        b.Property(e => e.CancelReason).HasMaxLength(500);

        b.HasOne(e => e.Company)
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(e => e.CurrentAccount)
            .WithMany()
            .HasForeignKey(e => e.CurrentAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(e => e.Lines)
            .WithOne(l => l.Invoice)
            .HasForeignKey(l => l.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // DB-side guarantee for numbering (CONTRACT §3.4): duplicate numbers fail with error 2601 → retry.
        b.HasIndex(e => new { e.CompanyId, e.Series, e.Number })
            .IsUnique()
            .HasFilter("[Number] IS NOT NULL")
            .HasDatabaseName("UX_Invoices_CompanyId_Series_Number");

        b.HasIndex(e => new { e.CompanyId, e.InvoiceDate }).HasDatabaseName("IX_Invoices_CompanyId_InvoiceDate");
        b.HasIndex(e => e.CurrentAccountId).HasDatabaseName("IX_Invoices_CurrentAccountId");
        b.HasIndex(e => e.Status).HasDatabaseName("IX_Invoices_Status");
        b.HasIndex(e => new { e.CompanyId, e.CreatedAt }).HasDatabaseName("IX_Invoices_CompanyId_CreatedAt");
    }
}
