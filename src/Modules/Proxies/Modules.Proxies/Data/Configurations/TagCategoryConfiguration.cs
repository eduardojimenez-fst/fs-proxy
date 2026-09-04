using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagCategoryConfiguration : IEntityTypeConfiguration<TagCategory>
{
    public void Configure(EntityTypeBuilder<TagCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagCategories");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasMany(x => x.Values).WithOne().HasForeignKey(x => x.TagCategoryId).OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(TagCategory.Values))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.DomainEvents);
    }
}
