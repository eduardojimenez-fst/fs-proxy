using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;

public sealed class DeleteManualProxyCommandValidator : AbstractValidator<DeleteManualProxyCommand>
{
    public DeleteManualProxyCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
