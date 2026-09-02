using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;

public sealed class AssignHealthCheckTargetToTagCommandValidator : AbstractValidator<AssignHealthCheckTargetToTagCommand>
{
    public AssignHealthCheckTargetToTagCommandValidator()
    {
        RuleFor(x => x.TagId).NotEmpty();
        RuleFor(x => x.HealthCheckTargetId).NotEmpty();
    }
}
