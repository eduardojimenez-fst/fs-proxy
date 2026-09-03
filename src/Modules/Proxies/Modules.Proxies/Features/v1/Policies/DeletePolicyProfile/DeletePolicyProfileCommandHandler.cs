using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;

public sealed class DeletePolicyProfileCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeletePolicyProfileCommand>
{
    public async ValueTask<Unit> Handle(DeletePolicyProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await dbContext.PolicyProfiles.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Policy profile {command.Id} not found.");
        // Restrict-delete FK (Task 2) means this throws a DbUpdateException if any TagPolicyAssignment
        // still references it — surface that as a 409 rather than a raw 500.
        bool inUse = await dbContext.Set<Domain.TagPolicyAssignment>().AnyAsync(x => x.PolicyProfileId == command.Id, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            throw new CustomException("This policy profile is still assigned to at least one tag. Unassign it first.", (IEnumerable<string>?)null, System.Net.HttpStatusCode.Conflict);
        }
        dbContext.PolicyProfiles.Remove(profile);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
