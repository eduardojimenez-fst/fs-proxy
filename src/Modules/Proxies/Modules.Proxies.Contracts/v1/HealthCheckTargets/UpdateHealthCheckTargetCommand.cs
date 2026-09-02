using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record UpdateHealthCheckTargetCommand(
    Guid Id, string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs) : ICommand;
