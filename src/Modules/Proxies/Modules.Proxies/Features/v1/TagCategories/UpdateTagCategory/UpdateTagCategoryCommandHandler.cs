using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;

public sealed class UpdateTagCategoryCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UpdateTagCategoryCommand>
{
    public async ValueTask<Unit> Handle(UpdateTagCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.Id} not found.");
        category.Rename(command.Name);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
