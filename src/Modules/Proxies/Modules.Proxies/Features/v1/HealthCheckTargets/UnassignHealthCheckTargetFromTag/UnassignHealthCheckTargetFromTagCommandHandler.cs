using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;

public sealed class UnassignHealthCheckTargetFromTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UnassignHealthCheckTargetFromTagCommand>
{
    public async ValueTask<Unit> Handle(UnassignHealthCheckTargetFromTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await dbContext.Set<TagHealthCheckTargetAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagHealthCheckTargetAssignment>().Remove(existing);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
}
