using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;

public sealed class DeleteProviderAccountCommandValidator : AbstractValidator<DeleteProviderAccountCommand>
{
    public DeleteProviderAccountCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
