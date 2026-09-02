using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record ListPolicyProfilesQuery : IQuery<IReadOnlyList<PolicyProfileDto>>;
