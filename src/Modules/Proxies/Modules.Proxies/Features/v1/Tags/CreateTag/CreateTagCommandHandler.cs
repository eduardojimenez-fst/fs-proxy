using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.Tags.CreateTag;

public sealed class CreateTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreateTagCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tag = Tag.Create(command.Name);
        dbContext.Tags.Add(tag);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return tag.Id;
    }
}
