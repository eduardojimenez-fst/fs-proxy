using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record ReportProxyFeedbackCommand(Guid ProxyId, UsageEventOutcome Outcome, string? Detail, string? ReporterIdentifier) : ICommand;
