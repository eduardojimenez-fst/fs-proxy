using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record RequestProxiesQuery(
    IReadOnlyList<string> Tags, int Count, ProxySelectionStrategy Strategy, string? SessionId)
    : IQuery<IReadOnlyList<ProxyConnectionDto>>;
