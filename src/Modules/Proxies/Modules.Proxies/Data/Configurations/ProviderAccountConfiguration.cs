using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProviderAccountConfiguration : IEntityTypeConfiguration<ProviderAccount>
{
    public void Configure(EntityTypeBuilder<ProviderAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProviderAccounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ProtectedCredentials).IsRequired();
        builder.Property(x => x.LastSyncStatus).HasMaxLength(1024);
        builder.HasIndex(x => x.ProviderType);
        builder.Ignore(x => x.DomainEvents);
    }
}
