using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;

public sealed class CreateTagCategoryCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreateTagCategoryCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateTagCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = TagCategory.Create(command.Name);
        dbContext.TagCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return category.Id;
    }
}
