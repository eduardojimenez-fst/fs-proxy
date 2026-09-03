using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ReportProxyFeedbackHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_RecordEvent_And_ResolveKnownApiClient()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        var reporter = ApiClient.Create("legacy-scraper", "hash");
        db.Proxies.Add(proxy);
        db.ApiClients.Add(reporter);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackCommandHandler(db, policyService);

        await sut.Handle(new ReportProxyFeedbackCommand(proxy.Id, UsageEventOutcome.Banned, "banned by mercadopublico.cl", reporter.Id.ToString()), CancellationToken.None);

        var stored = await db.ProxyUsageEvents.SingleAsync(e => e.ProxyId == proxy.Id);
        stored.Outcome.ShouldBe(UsageEventOutcome.Banned); // UsageEventOutcome now lives in Contracts (moved earlier in this task) — the `using FSH.Modules.Proxies.Contracts;` above already resolves it
        stored.ReportedByApiClientId.ShouldBe(reporter.Id);
        await policyService.Received(1).EvaluateAsync(proxy.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_RecordEvent_WithNoReporterId_When_IdentifierIsNotAKnownApiClient()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new ReportProxyFeedbackCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await sut.Handle(new ReportProxyFeedbackCommand(proxy.Id, UsageEventOutcome.Success, null, "some-tag-jwt-user-id"), CancellationToken.None);

        var stored = await db.ProxyUsageEvents.SingleAsync(e => e.ProxyId == proxy.Id);
        stored.ReportedByApiClientId.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_Throw_When_ProxyNotFound()
    {
        await using var db = CreateDb();
        var sut = new ReportProxyFeedbackCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(() =>
            sut.Handle(new ReportProxyFeedbackCommand(Guid.NewGuid(), UsageEventOutcome.Success, null, null), CancellationToken.None).AsTask());
    }
}
