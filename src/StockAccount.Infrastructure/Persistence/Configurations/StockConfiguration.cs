using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Configurations;

internal sealed class StockConfiguration : IEntityTypeConfiguration<Stock>
{
    public void Configure(EntityTypeBuilder<Stock> b)
    {
        b.ToTable("Stocks", t =>
        {
            t.HasCheckConstraint("CK_Stocks_VatRate", $"[VatRate] IN {ConfigurationExtensions.VatRatesSql}");
            t.HasCheckConstraint("CK_Stocks_UnitPrice", "[UnitPrice] >= 0");
            t.HasCheckConstraint("CK_Stocks_Unit", "[Unit] BETWEEN 1 AND 6");
            t.HasCheckConstraint("CK_Stocks_Code", "LEN([Code]) >= 1");
        });

        b.ConfigureAuditable();
        b.ConfigureSoftDelete();
        b.ConfigureRowVersion();

        b.Property(e => e.Code).HasMaxLength(50).IsRequired();
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.Property(e => e.Unit).IsRequired();
        b.Property(e => e.Barcode).Varchar(50);
        b.Property(e => e.VatRate).Rate().IsRequired();
        b.Property(e => e.UnitPrice).Price().IsRequired();
        b.Property(e => e.Description).HasMaxLength(500);
        b.Property(e => e.IsActive).IsRequired();
        b.Property(e => e.Balance).Quantity().IsRequired();

        b.HasOne(e => e.Company)
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(e => new { e.CompanyId, e.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Stocks_CompanyId_Code");

        b.HasIndex(e => new { e.CompanyId, e.Name }).HasDatabaseName("IX_Stocks_CompanyId_Name");
    }
}
