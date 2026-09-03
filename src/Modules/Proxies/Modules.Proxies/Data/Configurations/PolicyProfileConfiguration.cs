using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class PolicyProfileConfiguration : IEntityTypeConfiguration<PolicyProfile>
{
    public void Configure(EntityTypeBuilder<PolicyProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("PolicyProfiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Ignore(x => x.DomainEvents);
    }
}
