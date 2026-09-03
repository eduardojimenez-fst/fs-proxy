using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;

public sealed class CreateManualProxyCommandValidator : AbstractValidator<CreateManualProxyCommand>
{
    public CreateManualProxyCommandValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Protocol).IsInEnum();
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
