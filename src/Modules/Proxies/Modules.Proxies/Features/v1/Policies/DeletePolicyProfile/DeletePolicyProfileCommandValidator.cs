using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;

public sealed class DeletePolicyProfileCommandValidator : AbstractValidator<DeletePolicyProfileCommand>
{
    public DeletePolicyProfileCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
