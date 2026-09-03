using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;

public sealed class CreateProviderAccountCommandValidator : AbstractValidator<CreateProviderAccountCommand>
{
    public CreateProviderAccountCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.PlaintextCredentials).NotEmpty();
        RuleFor(x => x.ProviderType).IsInEnum();
    }
}
