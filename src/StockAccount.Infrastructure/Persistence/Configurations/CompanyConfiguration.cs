using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("Companies", t =>
        {
            t.HasCheckConstraint("CK_Companies_TaxNumber", ConfigurationExtensions.TaxNumberCheckSql("TaxNumber", nullable: false));
        });

        b.ConfigureAuditable();
        b.ConfigureSoftDelete();
        b.ConfigureRowVersion();

        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.Property(e => e.TaxNumber).Varchar(11).IsRequired();
        b.Property(e => e.TaxOffice).HasMaxLength(100);
        b.Property(e => e.Phone).Varchar(20);
        b.Property(e => e.Email).HasMaxLength(256);
        b.Property(e => e.Address).HasMaxLength(500);
        b.Property(e => e.City).HasMaxLength(100);
        b.Property(e => e.IsActive).IsRequired();

        b.HasIndex(e => e.TaxNumber)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Companies_TaxNumber");

        b.HasIndex(e => e.Name).HasDatabaseName("IX_Companies_Name");
    }
}
