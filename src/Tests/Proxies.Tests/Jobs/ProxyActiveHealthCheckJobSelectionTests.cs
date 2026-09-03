using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Jobs;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Jobs;

/// <summary>
/// Covers the job's DB-selection seam only — which proxies it picks up for probing. The probe
/// itself is live outbound HTTP and stays untested here; its pure decision seams live in
/// <see cref="ProxyHealthCheckOutcomeClassifier"/> and <see cref="ProxyProbeUriBuilder"/>.
/// Substituting the target resolver with an empty result stops each selected proxy short of any
/// network call while still recording which proxies were selected.
/// </summary>
public sealed class ProxyActiveHealthCheckJobSelectionTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Proxy SeedProxy(ProxiesDbContext db, string host, ProxyStatus status)
    {
        var proxy = Proxy.Create(ManualProviderAccount.Id, host, 8080, ProxyProtocol.Http, null, null, null);
        proxy.SetStatus(status);
        db.Proxies.Add(proxy);
        return proxy;
    }

    [Fact]
    public async Task RunAsync_Should_ProbeActiveAndTestingProxies_ButNotDisabledOrRetiredOnes()
    {
        await using var db = CreateDb();
        var active = SeedProxy(db, "1.1.1.1", ProxyStatus.Active);
        var testing = SeedProxy(db, "2.2.2.2", ProxyStatus.Testing);
        SeedProxy(db, "3.3.3.3", ProxyStatus.Disabled);
        SeedProxy(db, "4.4.4.4", ProxyStatus.Banned);
        SeedProxy(db, "5.5.5.5", ProxyStatus.Retired);
        await db.SaveChangesAsync();

        var resolvedIds = new List<Guid>();
        var targetResolver = Substitute.For<IHealthCheckTargetResolver>();
        targetResolver.ResolveTargetsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                resolvedIds.Add(call.Arg<Guid>());
                return Task.FromResult<IReadOnlyList<ResolvedHealthCheckTarget>>([]);
            });

        var sut = new ProxyActiveHealthCheckJob(
            db, targetResolver, Substitute.For<IProxyPasswordResolver>(),
            Substitute.For<IPolicyEvaluationService>(), NullLogger<ProxyActiveHealthCheckJob>.Instance);

        await sut.RunAsync(CancellationToken.None);

        resolvedIds.OrderBy(id => id).ShouldBe(new[] { active.Id, testing.Id }.OrderBy(id => id));
    }
}
