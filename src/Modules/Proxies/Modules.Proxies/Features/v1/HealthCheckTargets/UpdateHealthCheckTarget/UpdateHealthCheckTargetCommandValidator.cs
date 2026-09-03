using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;

public sealed class UpdateHealthCheckTargetCommandValidator : AbstractValidator<UpdateHealthCheckTargetCommand>
{
    public UpdateHealthCheckTargetCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TestUrl).NotEmpty().MaximumLength(2048).Must(u => Uri.TryCreate(u, UriKind.Absolute, out _)).WithMessage("TestUrl must be an absolute URL.");
        RuleFor(x => x.TimeoutMs).InclusiveBetween(500, 30000);
    }
}
