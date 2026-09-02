using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;

public sealed class UpdateHealthCheckTargetCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UpdateHealthCheckTargetCommand>
{
    public async ValueTask<Unit> Handle(UpdateHealthCheckTargetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = await dbContext.HealthCheckTargets.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Health check target {command.Id} not found.");
        target.Update(command.Name, command.TestUrl, command.ExpectedStatusCode, command.ExpectedBodyKeyword, command.TimeoutMs);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
