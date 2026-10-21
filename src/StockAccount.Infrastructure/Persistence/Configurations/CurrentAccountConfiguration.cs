using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Configurations;

internal sealed class CurrentAccountConfiguration : IEntityTypeConfiguration<CurrentAccount>
{
    public void Configure(EntityTypeBuilder<CurrentAccount> b)
    {
        b.ToTable("CurrentAccounts", t =>
        {
            t.HasCheckConstraint("CK_CurrentAccounts_Type", "[Type] BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_CurrentAccounts_TaxNumber", ConfigurationExtensions.TaxNumberCheckSql("TaxNumber", nullable: true));
            t.HasCheckConstraint("CK_CurrentAccounts_Code", "LEN([Code]) >= 1");
        });

        b.ConfigureAuditable();
        b.ConfigureSoftDelete();
        b.ConfigureRowVersion();

        b.Property(e => e.Code).HasMaxLength(50).IsRequired();
        b.Property(e => e.Title).HasMaxLength(200).IsRequired();
        b.Property(e => e.Type).IsRequired();
        b.Property(e => e.TaxNumber).Varchar(11);
        b.Property(e => e.TaxOffice).HasMaxLength(100);
        b.Property(e => e.Phone).Varchar(20);
        b.Property(e => e.Email).HasMaxLength(256);
        b.Property(e => e.Address).HasMaxLength(500);
        b.Property(e => e.City).HasMaxLength(100);
        b.Property(e => e.IsActive).IsRequired();
        b.Property(e => e.Balance).Money().IsRequired();

        b.HasOne(e => e.Company)
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(e => new { e.CompanyId, e.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_CurrentAccounts_CompanyId_Code");

        b.HasIndex(e => new { e.CompanyId, e.Title }).HasDatabaseName("IX_CurrentAccounts_CompanyId_Title");
    }
}
