using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ReportProxyFeedbackBatchHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Proxy NewProxy(string host) =>
        Proxy.Create(ManualProviderAccount.Id, host, 80, ProxyProtocol.Http, null, null, null);

    [Fact]
    public async Task Handle_Should_EvaluatePolicy_OncePerDistinctProxy_NotOncePerEvent()
    {
        await using var db = CreateDb();
        var first = NewProxy("1.1.1.1");
        var second = NewProxy("2.2.2.2");
        db.Proxies.AddRange(first, second);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);

        // Six events spread over two proxies.
        var events = new List<ProxyFeedbackEvent>
        {
            new(first.Id, UsageEventOutcome.Banned, null),
            new(first.Id, UsageEventOutcome.Timeout, null),
            new(first.Id, UsageEventOutcome.Failure, null),
            new(second.Id, UsageEventOutcome.Banned, null),
            new(second.Id, UsageEventOutcome.Failure, null),
            new(second.Id, UsageEventOutcome.Timeout, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        result.Accepted.ShouldBe(6);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(6);
        await policyService.Received(1).EvaluateAsync(first.Id, Arg.Any<CancellationToken>());
        await policyService.Received(1).EvaluateAsync(second.Id, Arg.Any<CancellationToken>());
        await policyService.Received(2).EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_NotEvaluatePolicy_ForProxiesWithOnlySuccesses()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);

        var events = new List<ProxyFeedbackEvent>
        {
            new(proxy.Id, UsageEventOutcome.Success, null),
            new(proxy.Id, UsageEventOutcome.Success, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        // The events are still recorded...
        result.Accepted.ShouldBe(2);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(2);
        // ...but PolicyEvaluationService only counts Outcome != Success, so evaluating is pure cost.
        await policyService.DidNotReceive().EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_EvaluatePolicyOnce_When_OneProxyHasBothSuccessAndNegativeOutcomes()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);

        var events = new List<ProxyFeedbackEvent>
        {
            new(proxy.Id, UsageEventOutcome.Success, null),
            new(proxy.Id, UsageEventOutcome.Banned, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        result.Accepted.ShouldBe(2);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(2);
        await policyService.Received(1).EvaluateAsync(proxy.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_AcceptKnownProxies_And_RejectUnknownOnes()
    {
        await using var db = CreateDb();
        var known = NewProxy("1.1.1.1");
        db.Proxies.Add(known);
        await db.SaveChangesAsync();
        var missing = Guid.NewGuid();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        var events = new List<ProxyFeedbackEvent>
        {
            new(known.Id, UsageEventOutcome.Banned, null),
            new(missing, UsageEventOutcome.Banned, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        result.Accepted.ShouldBe(1);
        result.Rejected.ShouldBe([missing]);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Handle_Should_ResolveKnownApiClientAsReporter()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        var reporter = ApiClient.Create("tag-scraper", "hash");
        db.Proxies.Add(proxy);
        db.ApiClients.Add(reporter);
        await db.SaveChangesAsync();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await sut.Handle(
            new ReportProxyFeedbackBatchCommand(
                [new ProxyFeedbackEvent(proxy.Id, UsageEventOutcome.Banned, "robot-check")],
                reporter.Id.ToString()),
            CancellationToken.None);

        var stored = await db.ProxyUsageEvents.SingleAsync();
        stored.ReportedByApiClientId.ShouldBe(reporter.Id);
        stored.Source.ShouldBe(UsageEventSource.ConsumerFeedback);
        stored.Detail.ShouldBe("robot-check");
    }

    [Fact]
    public async Task Handle_Should_LeaveReporterNull_When_IdentifierIsNotAKnownApiClient()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await sut.Handle(
            new ReportProxyFeedbackBatchCommand(
                [new ProxyFeedbackEvent(proxy.Id, UsageEventOutcome.Failure, null)],
                "a-jwt-user-id-not-an-api-client"),
            CancellationToken.None);

        (await db.ProxyUsageEvents.SingleAsync()).ReportedByApiClientId.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_ReturnEmptyResult_When_NoProxyInBatchExists()
    {
        await using var db = CreateDb();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);
        var missing = Guid.NewGuid();

        var result = await sut.Handle(
            new ReportProxyFeedbackBatchCommand([new ProxyFeedbackEvent(missing, UsageEventOutcome.Banned, null)], null),
            CancellationToken.None);

        // Unlike the single-event endpoint, an unknown proxy must NOT throw NotFoundException: the
        // client would retry the batch forever.
        result.Accepted.ShouldBe(0);
        result.Rejected.ShouldBe([missing]);
        await policyService.DidNotReceive().EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
