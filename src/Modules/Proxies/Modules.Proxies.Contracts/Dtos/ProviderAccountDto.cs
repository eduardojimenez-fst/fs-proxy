using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProviderAccountDto(
    Guid Id, string Name, ProxyProviderType ProviderType, bool IsEnabled,
    DateTime? LastSyncedAtUtc, string? LastSyncStatus, int ConsecutiveSyncFailures);
