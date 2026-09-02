using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;

public sealed class CreatePolicyProfileCommandValidator : AbstractValidator<CreatePolicyProfileCommand>
{
    public CreatePolicyProfileCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.FailureThreshold).GreaterThan(0);
        RuleFor(x => x.WindowMinutes).GreaterThan(0);
        RuleFor(x => x.MinDistinctReporters).GreaterThan(0);
    }
}
