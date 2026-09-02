using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;

public sealed class CreatePolicyProfileCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreatePolicyProfileCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreatePolicyProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = PolicyProfile.Create(command.Name, command.Type, command.FailureThreshold, command.WindowMinutes, command.MinDistinctReporters);
        dbContext.PolicyProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return profile.Id;
    }
}
