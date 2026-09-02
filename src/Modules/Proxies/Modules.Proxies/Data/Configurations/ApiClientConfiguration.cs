using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ApiClients");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ApiKeyHash).IsRequired().HasMaxLength(512);
        builder.HasIndex(x => x.ApiKeyHash).IsUnique();
        builder.Ignore(x => x.DomainEvents);
    }
}
