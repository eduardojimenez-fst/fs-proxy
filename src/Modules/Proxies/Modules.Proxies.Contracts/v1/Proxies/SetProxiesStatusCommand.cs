using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record SetProxiesStatusCommand(IReadOnlyList<Guid>? ProxyIds, Guid? TagId, ProxyStatus Status) : ICommand<int>;
