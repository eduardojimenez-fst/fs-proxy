using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ManualProxies;

public sealed record CreateManualProxyCommand(
    string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames) : ICommand<Guid>;
