using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ManualProxies;

public sealed record UpdateManualProxyCommand(
    Guid Id, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames) : ICommand;
