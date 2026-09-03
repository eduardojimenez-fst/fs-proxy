namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderSyncResult(IReadOnlyList<ProviderProxyRecord> Proxies, bool Success, string? ErrorMessage)
{
    public static ProviderSyncResult Ok(IReadOnlyList<ProviderProxyRecord> proxies) => new(proxies, true, null);
    public static ProviderSyncResult Failed(string errorMessage) => new([], false, errorMessage);
}
