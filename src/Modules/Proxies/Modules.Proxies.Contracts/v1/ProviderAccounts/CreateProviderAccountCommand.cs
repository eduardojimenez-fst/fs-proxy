using FSH.Modules.Proxies.Contracts;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record CreateProviderAccountCommand(
    string Name, ProxyProviderType ProviderType, string PlaintextCredentials) : ICommand<Guid>;
