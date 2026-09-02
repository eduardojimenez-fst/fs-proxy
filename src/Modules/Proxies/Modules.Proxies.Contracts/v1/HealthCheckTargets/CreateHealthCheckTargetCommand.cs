using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record CreateHealthCheckTargetCommand(
    string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs) : ICommand<Guid>;
