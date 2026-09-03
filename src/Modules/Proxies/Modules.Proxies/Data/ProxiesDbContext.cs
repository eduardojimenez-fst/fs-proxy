using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Persistence.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Data;

public sealed class ProxiesDbContext : BaseDbContext
{
    public const string Schema = "proxies";

    public ProxiesDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<ProxiesDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment) : base(multiTenantContextAccessor, options, settings, environment) { }

    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<Proxy> Proxies => Set<Proxy>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PolicyProfile> PolicyProfiles => Set<PolicyProfile>();
    public DbSet<HealthCheckTarget> HealthCheckTargets => Set<HealthCheckTarget>();
    public DbSet<ProxyUsageEvent> ProxyUsageEvents => Set<ProxyUsageEvent>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProxiesDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
