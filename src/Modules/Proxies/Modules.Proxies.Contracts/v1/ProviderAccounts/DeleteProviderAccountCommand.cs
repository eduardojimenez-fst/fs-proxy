using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record DeleteProviderAccountCommand(Guid Id) : ICommand;
