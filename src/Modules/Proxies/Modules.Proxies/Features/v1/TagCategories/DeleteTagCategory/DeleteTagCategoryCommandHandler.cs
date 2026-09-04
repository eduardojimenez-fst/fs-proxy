using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;

public sealed class DeleteTagCategoryCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteTagCategoryCommand>
{
    public async ValueTask<Unit> Handle(DeleteTagCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.Id} not found.");
        dbContext.TagCategories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
