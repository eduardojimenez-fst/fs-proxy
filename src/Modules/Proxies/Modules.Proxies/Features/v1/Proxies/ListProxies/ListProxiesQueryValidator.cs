using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;

public sealed class ListProxiesQueryValidator : AbstractValidator<ListProxiesQuery>
{
    public ListProxiesQueryValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.Geolocation).MaximumLength(10).When(x => x.Geolocation is not null);
    }
}
