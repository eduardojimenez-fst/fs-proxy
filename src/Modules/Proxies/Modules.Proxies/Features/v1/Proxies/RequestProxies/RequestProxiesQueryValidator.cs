using FluentValidation;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;

public sealed class RequestProxiesQueryValidator : AbstractValidator<RequestProxiesQuery>
{
    public RequestProxiesQueryValidator()
    {
        RuleForEach(x => x.Tags).NotEmpty();
        RuleFor(x => x.Count).InclusiveBetween(1, 50);
        RuleFor(x => x.Strategy).IsInEnum();
        RuleFor(x => x.SessionId).NotEmpty().When(x => x.Strategy == ProxySelectionStrategy.Sticky)
            .WithMessage("SessionId is required when Strategy is Sticky.");
    }
}
