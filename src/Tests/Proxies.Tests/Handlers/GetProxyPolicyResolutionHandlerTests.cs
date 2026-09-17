using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Policies.GetProxyPolicyResolution;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class GetProxyPolicyResolutionHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static GetProxyPolicyResolutionQueryHandler CreateSut(ProxiesDbContext db)
    {
        var options = Options.Create(new ProxiesOptions
        {
            DefaultHealthCheckTargetUrl = "https://www.google.com/generate_204",
            DefaultHealthCheckTimeoutMs = 5000,
        });
        return new GetProxyPolicyResolutionQueryHandler(db, new ProxyPolicyResolver(db), new HealthCheckTargetResolver(db, options));
    }

    private static async Task<(ProxiesDbContext Db, Proxy Proxy)> SeedProxyAsync()
    {
        var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var proxy = Proxy.Create(account.Id, "10.0.0.5", 3128, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        return (db, proxy);
    }

    [Fact]
    public async Task Handle_Should_ReportNoPolicy_And_DefaultTarget_When_ProxyHasNoTags()
    {
        var (db, proxy) = await SeedProxyAsync();
        await using var _ = db;

        var result = await CreateSut(db).Handle(new GetProxyPolicyResolutionQuery(proxy.Id), CancellationToken.None);

        // This is the "nothing will ever auto-disable this proxy" case the UI must make obvious.
        result.Policy.ShouldBeNull();
        result.FailuresInWindow.ShouldBeNull();
        result.Tags.ShouldBeEmpty();
        result.UsingDefaultHealthCheckTarget.ShouldBeTrue();
        result.HealthCheckTargets.Single().TestUrl.ShouldBe("https://www.google.com/generate_204");
        result.HealthCheckTargets.Single().Id.ShouldBeNull();
        result.HealthCheckTargets.Single().FromTag.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_PickMostRestrictivePolicy_AndNameTheTagItCameFrom()
    {
        var (db, proxy) = await SeedProxyAsync();
        await using var _ = db;
        var lenientTag = Tag.Create("pais:cl");
        var strictTag = Tag.Create("uso:critico");
        var lenient = PolicyProfile.Create("Lenient", PolicyProfileType.AutoDisable, 5, 30, 1);
        var strict = PolicyProfile.Create("Strict", PolicyProfileType.AutoDisableAndRenew, 3, 60, 1);
        db.Tags.AddRange(lenientTag, strictTag);
        db.PolicyProfiles.AddRange(lenient, strict);
        db.Set<TagPolicyAssignment>().AddRange(
            TagPolicyAssignment.Create(lenientTag.Id, lenient.Id),
            TagPolicyAssignment.Create(strictTag.Id, strict.Id));
        db.Set<ProxyTagAssignment>().AddRange(
            ProxyTagAssignment.Create(proxy.Id, lenientTag.Id),
            ProxyTagAssignment.Create(proxy.Id, strictTag.Id));
        await db.SaveChangesAsync();

        var result = await CreateSut(db).Handle(new GetProxyPolicyResolutionQuery(proxy.Id), CancellationToken.None);

        result.Policy!.Name.ShouldBe("Strict");
        result.PolicyFromTag.ShouldBe("uso:critico");
        result.Candidates.Count.ShouldBe(2);
        result.Candidates.Single(c => c.IsWinner).Profile.Name.ShouldBe("Strict");
        result.Tags.ShouldBe(["pais:cl", "uso:critico"]);
    }

    [Fact]
    public async Task Handle_Should_CountFailuresInWindow_TreatingEveryHealthCheckAsOneReporter()
    {
        var (db, proxy) = await SeedProxyAsync();
        await using var _ = db;
        var tag = Tag.Create("pais:cl");
        var policy = PolicyProfile.Create("Strict", PolicyProfileType.AutoDisable, 3, 60, 2);
        db.Tags.Add(tag);
        db.PolicyProfiles.Add(policy);
        db.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(tag.Id, policy.Id));
        db.Set<ProxyTagAssignment>().Add(ProxyTagAssignment.Create(proxy.Id, tag.Id));

        var clientId = Guid.NewGuid();
        var stale = ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, clientId, null);
        db.ProxyUsageEvents.AddRange(
            stale,
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Timeout, null, null, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Failure, null, null, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, clientId, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Success, null, clientId, null));
        await db.SaveChangesAsync();
        db.Entry(stale).Property(nameof(ProxyUsageEvent.OccurredAtUtc)).CurrentValue = DateTime.UtcNow.AddMinutes(-90);
        await db.SaveChangesAsync();

        var result = await CreateSut(db).Handle(new GetProxyPolicyResolutionQuery(proxy.Id), CancellationToken.None);

        // 3 non-success events inside the 60-minute window (the 90-minute-old one and the Success drop out).
        result.FailuresInWindow.ShouldBe(3);
        // Two health checks collapse into the single "system" reporter, plus the one API client.
        result.DistinctReportersInWindow.ShouldBe(2);
        result.WindowStartUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_Should_ReportTagAssignedTarget_InsteadOfTheDefault()
    {
        var (db, proxy) = await SeedProxyAsync();
        await using var _ = db;
        var tag = Tag.Create("pais:cl");
        var target = HealthCheckTarget.Create("Mercado Publico", "https://www.mercadopublico.cl", 200, "licitacion", 8000);
        db.Tags.Add(tag);
        db.HealthCheckTargets.Add(target);
        db.Set<TagHealthCheckTargetAssignment>().Add(TagHealthCheckTargetAssignment.Create(tag.Id, target.Id));
        db.Set<ProxyTagAssignment>().Add(ProxyTagAssignment.Create(proxy.Id, tag.Id));
        await db.SaveChangesAsync();

        var result = await CreateSut(db).Handle(new GetProxyPolicyResolutionQuery(proxy.Id), CancellationToken.None);

        result.UsingDefaultHealthCheckTarget.ShouldBeFalse();
        var resolved = result.HealthCheckTargets.Single();
        resolved.Name.ShouldBe("Mercado Publico");
        resolved.TestUrl.ShouldBe("https://www.mercadopublico.cl");
        resolved.ExpectedStatusCode.ShouldBe(200);
        resolved.ExpectedBodyKeyword.ShouldBe("licitacion");
        resolved.TimeoutMs.ShouldBe(8000);
        resolved.FromTag.ShouldBe("pais:cl");
    }

    [Fact]
    public async Task Handle_Should_Throw_When_ProxyDoesNotExist()
    {
        await using var db = CreateDb();

        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(
            async () => await CreateSut(db).Handle(new GetProxyPolicyResolutionQuery(Guid.NewGuid()), CancellationToken.None));
    }
}
