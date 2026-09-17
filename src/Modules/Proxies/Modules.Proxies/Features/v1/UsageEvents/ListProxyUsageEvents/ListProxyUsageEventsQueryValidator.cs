using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.UsageEvents;

namespace FSH.Modules.Proxies.Features.v1.UsageEvents.ListProxyUsageEvents;

public sealed class ListProxyUsageEventsQueryValidator : AbstractValidator<ListProxyUsageEventsQuery>
{
    public ListProxyUsageEventsQueryValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.ToUtc)
            .GreaterThanOrEqualTo(x => x.FromUtc!.Value)
            .When(x => x.FromUtc is not null && x.ToUtc is not null)
            .WithMessage("'To Utc' must be on or after 'From Utc'.");
    }
}
