using System.Net;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyOutcomeClassifierTests
{
    [Theory]
    // The destination answered. The tunnel worked, whatever it said.
    [InlineData(HttpStatusCode.OK, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.NotFound, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.BadRequest, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.InternalServerError, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.BadGateway, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProxyOutcome.Success)]
    // The destination recognized and rejected the IP.
    [InlineData(HttpStatusCode.Forbidden, ProxyOutcome.Banned)]
    [InlineData((HttpStatusCode)429, ProxyOutcome.Banned)]
    // The proxy itself rejected us.
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, ProxyOutcome.Failure)]
    [InlineData(HttpStatusCode.RequestTimeout, ProxyOutcome.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, ProxyOutcome.Timeout)]
    public void FromStatusCode_Should_Blame_The_Right_Party(HttpStatusCode status, ProxyOutcome expected)
    {
        ProxyOutcomeClassifier.FromStatusCode(status).ShouldBe(expected);
    }

    [Fact]
    public void FromException_Should_Return_Timeout_For_A_Cancelled_Task_With_A_Timeout_Inner()
    {
        // This is exactly the shape WebScraper.GetStringAsync already catches: the proxy accepted
        // the connection and went silent until HttpClient.Timeout fired.
        var exception = new TaskCanceledException("timed out", new TimeoutException());

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(ProxyOutcome.Timeout);
    }

    [Fact]
    public void FromException_Should_Return_Failure_For_A_Genuine_Cancellation()
    {
        // No TimeoutException inner: a shutting-down worker, not a bad proxy. Must NOT be
        // reported as Timeout or the policy engine punishes proxies for our own shutdowns.
        var exception = new TaskCanceledException("cancelled");

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(ProxyOutcome.Failure);
    }

    [Theory]
    [InlineData(WebExceptionStatus.Timeout, ProxyOutcome.Timeout)]
    [InlineData(WebExceptionStatus.ConnectFailure, ProxyOutcome.Failure)]
    [InlineData(WebExceptionStatus.ProxyNameResolutionFailure, ProxyOutcome.Failure)]
    public void FromException_Should_Classify_WebException_For_The_Legacy_Scrapers(
        WebExceptionStatus status, ProxyOutcome expected)
    {
        // The 4.8 scrapers are on WebRequest and throw WebException, not HttpRequestException.
        var exception = new WebException("boom", status);

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(expected);
    }

    [Fact]
    public void FromException_Should_Classify_An_HttpRequestException_By_Its_Status_Code()
    {
        // A 403 carried on the exception is the destination rejecting the IP, not a broken proxy.
        // Reporting Failure here would prescribe the wrong remedy.
        var exception = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(ProxyOutcome.Banned);
    }

    [Fact]
    public void FromResponse_Should_Defer_To_The_Status_Code_When_No_Custom_Classifier_Is_Given()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        ProxyOutcomeClassifier.FromResponse(response).ShouldBe(ProxyOutcome.Banned);
    }
}
