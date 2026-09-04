using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProxyConfiguration : IEntityTypeConfiguration<Proxy>
{
    public void Configure(EntityTypeBuilder<Proxy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Proxies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Host).IsRequired().HasMaxLength(255);
        builder.Property(x => x.Username).HasMaxLength(255);
        builder.Property(x => x.ProtectedPassword).HasMaxLength(1024);
        builder.Property(x => x.ExternalId).HasMaxLength(255);
        builder.Property(x => x.Geolocation).HasMaxLength(10);
        builder.Property(x => x.ProviderGrouping).HasMaxLength(255);
        builder.HasIndex(x => new { x.ProviderAccountId, x.ExternalId });
        builder.HasIndex(x => x.Status);
        builder.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => x.ProviderAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.TagAssignments).WithOne().HasForeignKey(x => x.ProxyId).OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(Proxy.TagAssignments))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.DomainEvents);
    }
}
