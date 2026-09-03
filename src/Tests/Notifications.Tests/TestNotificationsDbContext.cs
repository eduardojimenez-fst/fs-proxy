using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Notifications.Tests;

/// <summary>
/// EF-InMemory-friendly <see cref="NotificationsDbContext"/> factory for handler unit tests.
/// There is no existing <c>Notifications.Tests</c> project in this repo prior to this handler's
/// test — this is the first one — so this mirrors the closest established sibling patterns:
/// <c>Proxies.Tests.TestProxiesDbContext</c> (same static-factory-because-sealed-DbContext shape)
/// and <c>Webhooks.Tests.WebhookFanoutHandlerTests.CreateContext()</c> (same fixed
/// <see cref="IMultiTenantContextAccessor{AppTenantInfo}"/> substitute, needed here — unlike
/// Proxies — because <c>Notification</c> is NOT <c>IGlobalEntity</c>, so Finbuckle's per-tenant
/// query filter is live). Every read/write in a given test goes through the *same* accessor
/// instance, so whatever ambient tenant id it reports is consistently applied on both sides —
/// the exact id doesn't matter for these tests, only that it doesn't change mid-test.
///
/// <see cref="NotificationsDbContext"/> is <c>sealed</c>, so — like <c>ProxiesDbContext</c> and
/// <c>WebhookDbContext</c> — it cannot be subclassed for testing; use <see cref="Create"/>.
/// </summary>
internal static class TestNotificationsDbContext
{
    public static NotificationsDbContext Create(DbContextOptions<NotificationsDbContext> options) => new(
        multiTenantContextAccessor: new FixedMultiTenantContextAccessor(),
        options: options,
        settings: Options.Create(new DatabaseOptions()),
        environment: TestHostEnvironment.Instance);

    private sealed class FixedMultiTenantContextAccessor : IMultiTenantContextAccessor<AppTenantInfo>
    {
        private readonly IMultiTenantContext<AppTenantInfo> _context =
            new MultiTenantContext<AppTenantInfo>(new AppTenantInfo());

        public IMultiTenantContext<AppTenantInfo> MultiTenantContext => _context;

        IMultiTenantContext IMultiTenantContextAccessor.MultiTenantContext => _context;
    }
}

/// <summary>
/// Minimal <see cref="IHostEnvironment"/> stub for handler tests that need to construct a
/// <c>BaseDbContext</c>-derived context directly (see <c>Proxies.Tests.TestHostEnvironment</c>
/// and <c>WebhookFanoutHandlerTests.CreateContext()</c> for the same one-liner elsewhere).
/// </summary>
internal static class TestHostEnvironment
{
    public static IHostEnvironment Instance { get; } = CreateInstance();

    private static IHostEnvironment CreateInstance()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Development");
        return environment;
    }
}
