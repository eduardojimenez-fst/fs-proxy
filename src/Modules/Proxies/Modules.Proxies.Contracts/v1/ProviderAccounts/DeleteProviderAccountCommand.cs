using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

/// <summary>
/// <paramref name="Force"/> must be explicitly true to also delete proxies already synced from this
/// account (cascades to their tag assignments/usage events); otherwise the command fails with a
/// 409 Conflict naming how many proxies are in the way, letting the caller confirm before retrying.
/// </summary>
public sealed record DeleteProviderAccountCommand(Guid Id, bool Force = false) : ICommand;
