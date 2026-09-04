using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record UnassignProxyTagCommand(IReadOnlyList<Guid> ProxyIds, string TagName) : ICommand<int>;
