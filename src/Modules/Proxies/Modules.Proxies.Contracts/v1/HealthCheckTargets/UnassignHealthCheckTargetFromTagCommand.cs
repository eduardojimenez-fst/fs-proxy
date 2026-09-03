using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record UnassignHealthCheckTargetFromTagCommand(Guid TagId) : ICommand;
