using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;

public sealed class UnassignHealthCheckTargetFromTagCommandValidator : AbstractValidator<UnassignHealthCheckTargetFromTagCommand>
{
    public UnassignHealthCheckTargetFromTagCommandValidator() => RuleFor(x => x.TagId).NotEmpty();
}
