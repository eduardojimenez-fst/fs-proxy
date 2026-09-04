using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;

public sealed class SetProxyTagsCommandValidator : AbstractValidator<SetProxyTagsCommand>
{
    public SetProxyTagsCommandValidator()
    {
        RuleFor(x => x.ProxyId).NotEmpty();
        RuleFor(x => x.TagNames).NotNull();
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
