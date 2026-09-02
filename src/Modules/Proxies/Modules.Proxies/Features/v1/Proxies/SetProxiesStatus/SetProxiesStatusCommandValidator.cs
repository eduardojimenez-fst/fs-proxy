using FluentValidation;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxiesStatus;

public sealed class SetProxiesStatusCommandValidator : AbstractValidator<SetProxiesStatusCommand>
{
    public SetProxiesStatusCommandValidator()
    {
        RuleFor(x => x)
            .Must(x => (x.ProxyIds is { Count: > 0 }) ^ x.TagId.HasValue)
            .WithMessage("Provide exactly one of ProxyIds or TagId.");
        RuleFor(x => x.Status).Must(s => s is ProxyStatus.Active or ProxyStatus.Disabled)
            .WithMessage("Status must be Active or Disabled — other statuses are system-managed.");
    }
}
