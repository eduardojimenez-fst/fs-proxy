using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Multitenancy.Services;

/// <summary>
/// Returns the default database plus one target per distinct active per-tenant connection string.
///
/// Tenants sharing a database collapse to a single target: outbox rows are <c>IGlobalEntity</c>
/// and carry an explicit TenantId column, so one pass over a database drains every tenant living
/// in it. Draining once per tenant instead would multiply the work and have several passes race
/// for the same rows.
/// </summary>
public sealed class TenantStoreDrainTargetProvider : IEventingDrainTargetProvider
{
    private readonly IMultiTenantStore<AppTenantInfo> _tenantStore;

    public TenantStoreDrainTargetProvider(IMultiTenantStore<AppTenantInfo> tenantStore)
        => _tenantStore = tenantStore;

    public async Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default)
    {
        var tenants = await _tenantStore.GetAllAsync().ConfigureAwait(false);

        var dedicated = tenants
            .Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.ConnectionString))
            .GroupBy(t => t.ConnectionString, StringComparer.Ordinal)
            .Select(g => new EventingDrainTarget(g.First().Id, g.Key));

        // The default pass always runs: it covers every tenant without a dedicated database.
        return [new EventingDrainTarget(null, null), .. dedicated];
    }
}
