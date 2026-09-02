namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderRenewResult(bool Success, string? ErrorMessage, ProviderProxyRecord? UpdatedProxy)
{
    public static ProviderRenewResult Ok(ProviderProxyRecord updatedProxy) => new(true, null, updatedProxy);
    public static ProviderRenewResult Unsupported() => new(false, "Renewal is not supported by this provider.", null);
    public static ProviderRenewResult Failed(string errorMessage) => new(false, errorMessage, null);
}
