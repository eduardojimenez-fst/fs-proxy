namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProxyDto(
    Guid Id, string Host, int Port, ProxyProtocol Protocol, ProxyStatus Status,
    Guid ProviderAccountId, string ProviderAccountName, ProxyProviderType ProviderType,
    IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastRenewedAtUtc,
    string? Geolocation, string? ProviderGrouping, ProxyKind? Kind,
    // Several providers put the proxy's own egress IP in the auth username, so
    // operators read this column to identify a proxy. The password never leaves
    // the server; the username is not a secret on its own.
    string? Username = null,
    // Rolling 24h health signal, aggregated from ProxyUsageEvents. Lets the list show whether a
    // proxy is actually working without opening its timeline. Zeroes mean "no events", which for
    // a freshly-synced proxy is different from "healthy" — the UI renders that case as "—".
    int SuccessCount24h = 0,
    int FailureCount24h = 0,
    DateTime? LastEventAtUtc = null);
