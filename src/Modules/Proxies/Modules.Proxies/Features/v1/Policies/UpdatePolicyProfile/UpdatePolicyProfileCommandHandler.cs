using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.UpdatePolicyProfile;

public sealed class UpdatePolicyProfileCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UpdatePolicyProfileCommand>
{
    public async ValueTask<Unit> Handle(UpdatePolicyProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await dbContext.PolicyProfiles.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Policy profile {command.Id} not found.");
        profile.Update(command.Name, command.Type, command.FailureThreshold, command.WindowMinutes, command.MinDistinctReporters);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
