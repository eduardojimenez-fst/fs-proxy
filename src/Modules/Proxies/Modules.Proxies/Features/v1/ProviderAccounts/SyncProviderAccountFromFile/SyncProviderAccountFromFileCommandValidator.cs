using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;

public sealed class SyncProviderAccountFromFileCommandValidator : AbstractValidator<SyncProviderAccountFromFileCommand>
{
    public SyncProviderAccountFromFileCommandValidator()
    {
        RuleFor(x => x.ProviderAccountId).NotEmpty();
        RuleFor(x => x.FileContent).NotEmpty();
    }
}
