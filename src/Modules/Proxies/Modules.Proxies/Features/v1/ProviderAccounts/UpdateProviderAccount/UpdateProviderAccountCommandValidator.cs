using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;

public sealed class UpdateProviderAccountCommandValidator : AbstractValidator<UpdateProviderAccountCommand>
{
    public UpdateProviderAccountCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.PlaintextCredentials).MaximumLength(4096);
    }
}
