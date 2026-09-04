using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;

public sealed class UnassignProxyTagCommandValidator : AbstractValidator<UnassignProxyTagCommand>
{
    public UnassignProxyTagCommandValidator()
    {
        RuleFor(x => x.ProxyIds).NotEmpty();
        RuleFor(x => x.TagName).NotEmpty().MaximumLength(255);
    }
}
