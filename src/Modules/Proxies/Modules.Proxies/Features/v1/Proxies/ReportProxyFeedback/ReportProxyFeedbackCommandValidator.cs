using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;

public sealed class ReportProxyFeedbackCommandValidator : AbstractValidator<ReportProxyFeedbackCommand>
{
    public ReportProxyFeedbackCommandValidator()
    {
        RuleFor(x => x.ProxyId).NotEmpty();
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Detail).MaximumLength(2048);
    }
}
