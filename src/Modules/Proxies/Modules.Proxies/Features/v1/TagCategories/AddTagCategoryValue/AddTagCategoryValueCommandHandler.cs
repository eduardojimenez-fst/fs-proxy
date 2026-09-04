using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;

public sealed class AddTagCategoryValueCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AddTagCategoryValueCommand>
{
    public async ValueTask<Unit> Handle(AddTagCategoryValueCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.Include(x => x.Values)
            .FirstOrDefaultAsync(x => x.Id == command.TagCategoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.TagCategoryId} not found.");
        category.AddValue(command.Value);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
