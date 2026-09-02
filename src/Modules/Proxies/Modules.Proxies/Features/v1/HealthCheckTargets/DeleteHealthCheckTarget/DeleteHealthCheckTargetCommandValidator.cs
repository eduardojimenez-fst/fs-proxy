using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;

public sealed class DeleteHealthCheckTargetCommandValidator : AbstractValidator<DeleteHealthCheckTargetCommand>
{
    public DeleteHealthCheckTargetCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
