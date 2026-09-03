using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;

public sealed class AssignPolicyToTagCommandValidator : AbstractValidator<AssignPolicyToTagCommand>
{
    public AssignPolicyToTagCommandValidator()
    {
        RuleFor(x => x.TagId).NotEmpty();
        RuleFor(x => x.PolicyProfileId).NotEmpty();
    }
}
