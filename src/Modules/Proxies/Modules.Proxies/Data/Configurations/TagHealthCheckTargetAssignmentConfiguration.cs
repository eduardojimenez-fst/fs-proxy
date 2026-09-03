using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagHealthCheckTargetAssignmentConfiguration : IEntityTypeConfiguration<TagHealthCheckTargetAssignment>
{
    public void Configure(EntityTypeBuilder<TagHealthCheckTargetAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagHealthCheckTargetAssignments");
        builder.HasKey(x => x.TagId);
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<HealthCheckTarget>().WithMany().HasForeignKey(x => x.HealthCheckTargetId).OnDelete(DeleteBehavior.Restrict);
    }
}
