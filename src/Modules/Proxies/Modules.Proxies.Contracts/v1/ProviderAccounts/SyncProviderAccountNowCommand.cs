using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record SyncProviderAccountNowCommand(Guid ProviderAccountId) : ICommand<int>;
