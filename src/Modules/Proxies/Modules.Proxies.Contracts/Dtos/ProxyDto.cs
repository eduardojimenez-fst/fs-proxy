namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProxyDto(
    Guid Id, string Host, int Port, ProxyProtocol Protocol, ProxyStatus Status,
    Guid ProviderAccountId, string ProviderAccountName, ProxyProviderType ProviderType,
    IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastRenewedAtUtc);
