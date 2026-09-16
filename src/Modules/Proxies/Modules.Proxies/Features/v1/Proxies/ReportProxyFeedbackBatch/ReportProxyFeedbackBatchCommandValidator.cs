using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;

public sealed class ReportProxyFeedbackBatchCommandValidator : AbstractValidator<ReportProxyFeedbackBatchCommand>
{
    /// <summary>
    /// Ceiling on one submission. Chosen to sit in the same order of magnitude as the cap of 50 on
    /// <c>RequestProxiesQuery.Count</c>: large enough that a client flushing every ten seconds never
    /// splits a normal batch, small enough that one request cannot fan out into an unbounded number
    /// of inserts and policy evaluations.
    /// </summary>
    private const int MaxBatchSize = 200;

    public ReportProxyFeedbackBatchCommandValidator()
    {
        RuleFor(x => x.Events).NotEmpty();
        RuleFor(x => x.Events.Count).InclusiveBetween(1, MaxBatchSize).When(x => x.Events is not null);
        RuleForEach(x => x.Events).ChildRules(each =>
        {
            each.RuleFor(e => e.ProxyId).NotEmpty();
            each.RuleFor(e => e.Outcome).IsInEnum();
            each.RuleFor(e => e.Detail).MaximumLength(2048);
        });
    }
}
