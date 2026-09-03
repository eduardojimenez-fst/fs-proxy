using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProxyTagAssignmentConfiguration : IEntityTypeConfiguration<ProxyTagAssignment>
{
    public void Configure(EntityTypeBuilder<ProxyTagAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProxyTagAssignments");
        builder.HasKey(x => new { x.ProxyId, x.TagId });
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}
