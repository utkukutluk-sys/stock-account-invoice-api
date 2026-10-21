using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StockAccount.Application.Common.Identity;

namespace StockAccount.Infrastructure.Persistence.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        // Table name AspNetUsers and the unique UserNameIndex come from IdentityDbContext.
        b.Property(e => e.FullName).HasMaxLength(150).IsRequired();
        b.Property(e => e.IsActive).IsRequired();
        b.Property(e => e.CreatedAt).IsRequired();

        b.HasOne(e => e.Company)
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(e => e.CurrentAccount)
            .WithMany()
            .HasForeignKey(e => e.CurrentAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // A current account can be linked to at most one portal user.
        b.HasIndex(e => e.CurrentAccountId)
            .IsUnique()
            .HasFilter("[CurrentAccountId] IS NOT NULL")
            .HasDatabaseName("UX_AspNetUsers_CurrentAccountId");

        // Unique email (Identity's EmailIndex is non-unique). UserName = Email, so this mirrors UserNameIndex.
        b.HasIndex(e => e.NormalizedEmail)
            .IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL")
            .HasDatabaseName("EmailIndex");

        b.HasIndex(e => e.CompanyId).HasDatabaseName("IX_AspNetUsers_CompanyId");

        b.HasMany(e => e.RefreshTokens)
            .WithOne(t => t.User)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).UseIdentityColumn();

        b.Property(e => e.TokenHash).Varchar(64).IsRequired();
        b.Property(e => e.ExpiresAt).IsRequired();
        b.Property(e => e.CreatedAt).IsRequired();
        b.Property(e => e.CreatedByIp).Varchar(45);
        b.Property(e => e.ReplacedByTokenHash).Varchar(64);
        b.Property(e => e.RevokedReason).HasMaxLength(100);

        b.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName("UX_RefreshTokens_TokenHash");
        b.HasIndex(e => e.UserId).HasDatabaseName("IX_RefreshTokens_UserId");
    }
}
