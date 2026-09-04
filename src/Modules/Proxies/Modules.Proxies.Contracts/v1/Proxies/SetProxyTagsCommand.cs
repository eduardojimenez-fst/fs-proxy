using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record SetProxyTagsCommand(Guid ProxyId, IReadOnlyList<string> TagNames) : ICommand;
