using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;

public sealed class UnassignPolicyFromTagCommandValidator : AbstractValidator<UnassignPolicyFromTagCommand>
{
    public UnassignPolicyFromTagCommandValidator() => RuleFor(x => x.TagId).NotEmpty();
}
