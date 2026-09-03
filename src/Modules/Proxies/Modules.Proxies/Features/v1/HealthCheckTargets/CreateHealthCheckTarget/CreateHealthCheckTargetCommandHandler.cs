using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;

public sealed class CreateHealthCheckTargetCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreateHealthCheckTargetCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateHealthCheckTargetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = HealthCheckTarget.Create(command.Name, command.TestUrl, command.ExpectedStatusCode, command.ExpectedBodyKeyword, command.TimeoutMs);
        dbContext.HealthCheckTargets.Add(target);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return target.Id;
    }
}
