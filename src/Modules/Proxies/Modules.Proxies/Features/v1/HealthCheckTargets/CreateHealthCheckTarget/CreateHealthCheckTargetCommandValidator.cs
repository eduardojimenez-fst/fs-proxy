using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;

public sealed class CreateHealthCheckTargetCommandValidator : AbstractValidator<CreateHealthCheckTargetCommand>
{
    public CreateHealthCheckTargetCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TestUrl).NotEmpty().MaximumLength(2048).Must(u => Uri.TryCreate(u, UriKind.Absolute, out _)).WithMessage("TestUrl must be an absolute URL.");
        RuleFor(x => x.TimeoutMs).InclusiveBetween(500, 30000);
    }
}
