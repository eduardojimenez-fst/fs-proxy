using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;

public sealed class AssignHealthCheckTargetToTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AssignHealthCheckTargetToTagCommand>
{
    public async ValueTask<Unit> Handle(AssignHealthCheckTargetToTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool tagExists = await dbContext.Tags.AnyAsync(x => x.Id == command.TagId, cancellationToken).ConfigureAwait(false);
        if (!tagExists) throw new NotFoundException($"Tag {command.TagId} not found.");
        bool targetExists = await dbContext.HealthCheckTargets.AnyAsync(x => x.Id == command.HealthCheckTargetId, cancellationToken).ConfigureAwait(false);
        if (!targetExists) throw new NotFoundException($"Health check target {command.HealthCheckTargetId} not found.");

        var existing = await dbContext.Set<TagHealthCheckTargetAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagHealthCheckTargetAssignment>().Remove(existing);
        }
        dbContext.Set<TagHealthCheckTargetAssignment>().Add(TagHealthCheckTargetAssignment.Create(command.TagId, command.HealthCheckTargetId));

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
