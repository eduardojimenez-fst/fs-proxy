using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record AssignHealthCheckTargetToTagCommand(Guid TagId, Guid HealthCheckTargetId) : ICommand;
