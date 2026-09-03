using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;

public sealed class DeleteTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteTagCommand>
{
    public async ValueTask<Unit> Handle(DeleteTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tag = await dbContext.Tags.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag {command.Id} not found.");
        dbContext.Tags.Remove(tag);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
