using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ManualProxies;

public sealed record DeleteManualProxyCommand(Guid Id) : ICommand;
