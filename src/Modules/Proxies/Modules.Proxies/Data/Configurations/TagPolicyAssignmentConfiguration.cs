using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagPolicyAssignmentConfiguration : IEntityTypeConfiguration<TagPolicyAssignment>
{
    public void Configure(EntityTypeBuilder<TagPolicyAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagPolicyAssignments");
        builder.HasKey(x => x.TagId);
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PolicyProfile>().WithMany().HasForeignKey(x => x.PolicyProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
