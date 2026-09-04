using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;

public sealed class AssignProxyTagCommandValidator : AbstractValidator<AssignProxyTagCommand>
{
    public AssignProxyTagCommandValidator()
    {
        RuleFor(x => x.ProxyIds).NotEmpty();
        RuleFor(x => x.TagName).NotEmpty().MaximumLength(128);
    }
}
