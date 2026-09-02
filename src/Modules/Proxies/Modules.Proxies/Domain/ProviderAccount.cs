using FSH.Framework.Core.Domain;
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Domain;

/// <see cref="IGlobalEntity"/>: this is an internal single-tenant ops tool — proxies are not
/// per-tenant data.
public sealed class ProviderAccount : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public ProxyProviderType ProviderType { get; private set; }
    public string ProtectedCredentials { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public DateTime? LastSyncedAtUtc { get; private set; }
    public string? LastSyncStatus { get; private set; }
    public int ConsecutiveSyncFailures { get; private set; }

    private ProviderAccount() { }

    public static ProviderAccount Create(string name, ProxyProviderType providerType, string protectedCredentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCredentials);
        return new ProviderAccount
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            ProviderType = providerType,
            ProtectedCredentials = protectedCredentials,
            IsEnabled = true
        };
    }

    public void UpdateCredentials(string protectedCredentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCredentials);
        ProtectedCredentials = protectedCredentials;
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void RecordSyncResult(bool success, string? statusMessage)
    {
        LastSyncedAtUtc = DateTime.UtcNow;
        LastSyncStatus = statusMessage;
        ConsecutiveSyncFailures = success ? 0 : ConsecutiveSyncFailures + 1;
    }
}
