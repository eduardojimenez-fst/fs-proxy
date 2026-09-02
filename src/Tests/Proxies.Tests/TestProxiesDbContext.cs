using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Proxies.Tests;

/// <summary>
/// EF-InMemory-friendly <see cref="ProxiesDbContext"/> factory for handler unit tests. Every
/// entity in this module implements <c>IGlobalEntity</c>, so it is exempt from Finbuckle's
/// tenant query filter — no live tenant context is required to read/write rows, only a
/// non-null accessor whose <see cref="AppTenantInfo"/> has no connection string, so
/// <c>BaseDbContext.OnConfiguring</c>'s tenant-connection-routing branch is skipped and the
/// <c>UseInMemoryDatabase</c> configuration supplied by the test is left untouched.
///
/// This is a static factory rather than a subclass because <see cref="ProxiesDbContext"/> is
/// <c>sealed</c> — like every other module's DbContext in this repo (TicketsDbContext,
/// WebhookDbContext, ChatDbContext, ...) — so it cannot be derived from. Use
/// <see cref="Create"/> to obtain an instance (<c>TestProxiesDbContext.Create(options)</c>),
/// not <c>new TestProxiesDbContext(options)</c>.
///
/// There is also no <c>FSH.Framework.Shared.Multitenancy.NullMultiTenantContextAccessor</c> in
/// the installed FSH.Framework.Shared package (version pinned via FshFrameworkVersion in
/// Directory.Packages.props) — confirmed by inspecting the shipped assembly, which contains no
/// multitenancy-accessor types at all (those live in FSH.Framework.Persistence/Finbuckle
/// instead). This mirrors the pattern already used by
/// <c>WebhookFanoutHandlerTests.CreateContext()</c>
/// (src/Tests/Webhooks.Tests/Services/WebhookFanoutHandlerTests.cs), which likewise constructs
/// its (also sealed) <c>WebhookDbContext</c> directly rather than subclassing it: a substitute
/// <see cref="IHostEnvironment"/> plus a minimal hand-written
/// <see cref="IMultiTenantContextAccessor{AppTenantInfo}"/>.
/// </summary>
internal static class TestProxiesDbContext
{
    public static ProxiesDbContext Create(DbContextOptions<ProxiesDbContext> options) => new(
        multiTenantContextAccessor: new NoopMultiTenantContextAccessor(),
        options: options,
        settings: Options.Create(new DatabaseOptions()),
        environment: TestHostEnvironment.Instance);

    private sealed class NoopMultiTenantContextAccessor : IMultiTenantContextAccessor<AppTenantInfo>
    {
        private readonly IMultiTenantContext<AppTenantInfo> _context =
            new MultiTenantContext<AppTenantInfo>(new AppTenantInfo());

        public IMultiTenantContext<AppTenantInfo> MultiTenantContext => _context;

        IMultiTenantContext IMultiTenantContextAccessor.MultiTenantContext => _context;
    }
}

/// <summary>
/// Minimal <see cref="IHostEnvironment"/> stub for handler tests that need to construct a
/// <c>BaseDbContext</c>-derived context directly. NSubstitute can fake <see cref="IHostEnvironment"/>
/// directly, so this just centralizes that one-liner (see also
/// WebhookFanoutHandlerTests.CreateContext(), which inlines the same substitute).
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
