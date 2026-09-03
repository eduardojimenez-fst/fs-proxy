using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProxyUsageEventConfiguration : IEntityTypeConfiguration<ProxyUsageEvent>
{
    public void Configure(EntityTypeBuilder<ProxyUsageEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProxyUsageEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Detail).HasMaxLength(2048);
        // Read pattern is always "events for proxy X in the last N minutes" (policy engine, Task 18).
        builder.HasIndex(x => new { x.ProxyId, x.OccurredAtUtc });
        builder.HasOne<Proxy>().WithMany().HasForeignKey(x => x.ProxyId).OnDelete(DeleteBehavior.Cascade);
    }
}
