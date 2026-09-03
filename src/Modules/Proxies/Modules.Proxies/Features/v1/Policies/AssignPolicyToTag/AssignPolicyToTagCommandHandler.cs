using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;

public sealed class AssignPolicyToTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AssignPolicyToTagCommand>
{
    public async ValueTask<Unit> Handle(AssignPolicyToTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool tagExists = await dbContext.Tags.AnyAsync(x => x.Id == command.TagId, cancellationToken).ConfigureAwait(false);
        if (!tagExists) throw new NotFoundException($"Tag {command.TagId} not found.");
        bool policyExists = await dbContext.PolicyProfiles.AnyAsync(x => x.Id == command.PolicyProfileId, cancellationToken).ConfigureAwait(false);
        if (!policyExists) throw new NotFoundException($"Policy profile {command.PolicyProfileId} not found.");

        var existing = await dbContext.Set<TagPolicyAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagPolicyAssignment>().Remove(existing);
        }
        dbContext.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(command.TagId, command.PolicyProfileId));

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
