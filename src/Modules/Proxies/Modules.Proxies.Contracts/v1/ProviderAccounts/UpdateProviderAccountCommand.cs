using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record UpdateProviderAccountCommand(
    Guid Id, string Name, string? PlaintextCredentials, bool IsEnabled) : ICommand;
