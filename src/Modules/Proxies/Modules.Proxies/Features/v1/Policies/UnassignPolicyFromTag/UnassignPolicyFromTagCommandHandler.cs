using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;

public sealed class UnassignPolicyFromTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UnassignPolicyFromTagCommand>
{
    public async ValueTask<Unit> Handle(UnassignPolicyFromTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await dbContext.Set<TagPolicyAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagPolicyAssignment>().Remove(existing);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
}
