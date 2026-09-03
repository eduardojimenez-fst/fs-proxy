using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class HealthCheckTargetConfiguration : IEntityTypeConfiguration<HealthCheckTarget>
{
    public void Configure(EntityTypeBuilder<HealthCheckTarget> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("HealthCheckTargets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.TestUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.ExpectedBodyKeyword).HasMaxLength(256);
        builder.Ignore(x => x.DomainEvents);
    }
}
