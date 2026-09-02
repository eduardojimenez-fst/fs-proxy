namespace FSH.Modules.Proxies.Domain;

/// <summary>Every manually-entered Proxy row's ProviderAccountId points at this fixed account.</summary>
public static class ManualProviderAccount
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
