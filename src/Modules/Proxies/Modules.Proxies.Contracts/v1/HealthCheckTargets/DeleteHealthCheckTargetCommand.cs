using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record DeleteHealthCheckTargetCommand(Guid Id) : ICommand;
