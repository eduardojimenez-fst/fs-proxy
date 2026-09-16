using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record ReportProxyFeedbackBatchCommand(
    IReadOnlyList<ProxyFeedbackEvent> Events, string? ReporterIdentifier) : ICommand<BatchFeedbackResult>;
