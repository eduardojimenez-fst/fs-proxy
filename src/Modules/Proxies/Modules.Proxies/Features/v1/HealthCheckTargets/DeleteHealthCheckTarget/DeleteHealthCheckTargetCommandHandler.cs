using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;

public sealed class DeleteHealthCheckTargetCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteHealthCheckTargetCommand>
{
    public async ValueTask<Unit> Handle(DeleteHealthCheckTargetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = await dbContext.HealthCheckTargets.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Health check target {command.Id} not found.");
        bool inUse = await dbContext.Set<Domain.TagHealthCheckTargetAssignment>().AnyAsync(x => x.HealthCheckTargetId == command.Id, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            throw new CustomException("This health check target is still assigned to at least one tag. Unassign it first.", (IEnumerable<string>?)null, System.Net.HttpStatusCode.Conflict);
        }
        dbContext.HealthCheckTargets.Remove(target);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
