using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;

public sealed class UpdateManualProxyCommandValidator : AbstractValidator<UpdateManualProxyCommand>
{
    public UpdateManualProxyCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
