using System.Net;
using FSH.Modules.Proxies.Domain;
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
}
