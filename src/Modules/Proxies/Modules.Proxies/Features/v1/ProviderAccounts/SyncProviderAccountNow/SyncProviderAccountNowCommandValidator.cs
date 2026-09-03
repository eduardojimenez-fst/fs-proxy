using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;

public sealed class SyncProviderAccountNowCommandValidator : AbstractValidator<SyncProviderAccountNowCommand>
{
    public SyncProviderAccountNowCommandValidator() => RuleFor(x => x.ProviderAccountId).NotEmpty();
}
