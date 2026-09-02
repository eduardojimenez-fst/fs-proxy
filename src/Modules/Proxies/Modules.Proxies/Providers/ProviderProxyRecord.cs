using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderProxyRecord(
    string ExternalId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? Password, bool IsActive);
