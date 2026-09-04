using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagCategoryValueConfiguration : IEntityTypeConfiguration<TagCategoryValue>
{
    public void Configure(EntityTypeBuilder<TagCategoryValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagCategoryValues");
        builder.HasKey(x => new { x.TagCategoryId, x.Value });
        builder.Property(x => x.Value).IsRequired().HasMaxLength(128);
    }
}
