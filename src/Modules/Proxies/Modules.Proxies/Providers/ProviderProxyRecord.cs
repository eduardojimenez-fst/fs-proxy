using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderProxyRecord(
    string ExternalId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? Password, bool IsActive,
    string? Geolocation = null, string? ProviderGrouping = null);
