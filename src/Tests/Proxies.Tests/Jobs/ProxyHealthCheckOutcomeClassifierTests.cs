using System.Net;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Jobs;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Jobs;

public sealed class ProxyHealthCheckOutcomeClassifierTests
{
    [Fact]
    public void Classify_Should_ReturnTimeout_When_RequestTimedOut() =>
        ProxyHealthCheckOutcomeClassifier.Classify(timedOut: true, null, null, null, null).ShouldBe(UsageEventOutcome.Timeout);

    [Fact]
    public void Classify_Should_ReturnSuccess_When_NoExpectationsAndStatusIs2xx() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.NoContent, null, null, null).ShouldBe(UsageEventOutcome.Success);

    [Fact]
    public void Classify_Should_ReturnFailure_When_StatusDoesNotMatchExpected() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.NotFound, null, 200, null).ShouldBe(UsageEventOutcome.Failure);

    [Fact]
    public void Classify_Should_ReturnSuccess_When_StatusAndBodyKeywordMatch() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.OK, "licitaciones publicas activas", 200, "licitaciones").ShouldBe(UsageEventOutcome.Success);

    [Fact]
    public void Classify_Should_ReturnFailure_When_BodyKeywordMissing() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.OK, "pagina de error", 200, "licitaciones").ShouldBe(UsageEventOutcome.Failure);

    [Fact]
    public void ShouldPromoteToActive_Should_BeTrue_When_TestingProxyProbesSuccessfully() =>
        ProxyHealthCheckOutcomeClassifier.ShouldPromoteToActive(ProxyStatus.Testing, UsageEventOutcome.Success).ShouldBeTrue();

    [Theory]
    [InlineData(UsageEventOutcome.Failure)]
    [InlineData(UsageEventOutcome.Timeout)]
    [InlineData(UsageEventOutcome.Banned)]
    public void ShouldPromoteToActive_Should_BeFalse_When_TestingProxyProbeFails(UsageEventOutcome outcome) =>
        ProxyHealthCheckOutcomeClassifier.ShouldPromoteToActive(ProxyStatus.Testing, outcome).ShouldBeFalse();

    [Theory]
    [InlineData(ProxyStatus.Active)]
    [InlineData(ProxyStatus.Disabled)]
    [InlineData(ProxyStatus.Banned)]
    [InlineData(ProxyStatus.Retired)]
    public void ShouldPromoteToActive_Should_BeFalse_When_ProxyIsNotTesting(ProxyStatus status) =>
        ProxyHealthCheckOutcomeClassifier.ShouldPromoteToActive(status, UsageEventOutcome.Success).ShouldBeFalse();
}
