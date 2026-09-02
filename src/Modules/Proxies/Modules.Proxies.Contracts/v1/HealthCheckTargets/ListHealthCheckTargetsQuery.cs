using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record ListHealthCheckTargetsQuery : IQuery<IReadOnlyList<HealthCheckTargetDto>>;
