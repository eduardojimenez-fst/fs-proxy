using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class PolicyEvaluationServiceTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Proxy Proxy, Tag Tag, PolicyProfile Policy)> SeedProxyWithPolicyAsync(
        FSH.Modules.Proxies.Data.ProxiesDbContext db, PolicyProfileType type, int threshold, int minReporters)
    {
        var tag = Tag.Create("pais:cl");
        var policy = PolicyProfile.Create("critical", type, threshold, windowMinutes: 60, minDistinctReporters: minReporters);
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 8080, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(tag.Id);
        db.Tags.Add(tag);
        db.PolicyProfiles.Add(policy);
        db.Proxies.Add(proxy);
        db.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(tag.Id, policy.Id));
        await db.SaveChangesAsync();
        return (proxy, tag, policy);
    }

    [Fact]
    public async Task EvaluateAsync_Should_Disable_When_ThresholdAndReportersReached()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.AutoDisable, threshold: 2, minReporters: 2);
        var reporterA = ApiClient.Create("scraper-a", "hash-a");
        var reporterB = ApiClient.Create("scraper-b", "hash-b");
        db.ApiClients.AddRange(reporterA, reporterB);
        db.ProxyUsageEvents.AddRange(
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporterA.Id, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporterB.Id, null));
        await db.SaveChangesAsync();
        var renewalService = Substitute.For<IProxyRenewalService>();
        var sut = new PolicyEvaluationService(db, renewalService);

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Disabled);
        await renewalService.DidNotReceive().TriggerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_Should_TriggerRenewal_When_PolicyIsAutoDisableAndRenew()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.AutoDisableAndRenew, threshold: 1, minReporters: 1);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ApiClients.Add(reporter);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var renewalService = Substitute.For<IProxyRenewalService>();
        var sut = new PolicyEvaluationService(db, renewalService);

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        await renewalService.Received(1).TriggerAsync(proxy.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_ThresholdNotReached()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.AutoDisable, threshold: 5, minReporters: 1);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ApiClients.Add(reporter);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var sut = new PolicyEvaluationService(db, Substitute.For<IProxyRenewalService>());

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_PolicyIsManual()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.Manual, threshold: 1, minReporters: 1);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ApiClients.Add(reporter);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var sut = new PolicyEvaluationService(db, Substitute.For<IProxyRenewalService>());

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_NoPolicyAssigned()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 8080, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, null, null));
        await db.SaveChangesAsync();
        var sut = new PolicyEvaluationService(db, Substitute.For<IProxyRenewalService>());

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }
}
