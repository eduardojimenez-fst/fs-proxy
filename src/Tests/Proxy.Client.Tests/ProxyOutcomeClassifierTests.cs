using System;
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
    [InlineData(HttpStatusCode.MovedPermanently, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.NotFound, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.BadRequest, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.InternalServerError, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.BadGateway, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProxyOutcome.Success)]
    // I2: 408 is the ORIGIN server's own response (its own idle-timeout decision — nothing to do
    // with the proxy in front of it); 504 is the destination's own gateway answering with an error
    // status, exactly like 502/503. Both must be Success, not Timeout — an overloaded origin emits
    // 502/503/504 near-interchangeably, and classifying 504 differently from its siblings would
    // report the same bad afternoon as Success via one status and Timeout via another, quarantining
    // every proxy that happens to touch it.
    [InlineData(HttpStatusCode.RequestTimeout, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.GatewayTimeout, ProxyOutcome.Success)]
    // The destination recognized and rejected the IP.
    [InlineData(HttpStatusCode.Forbidden, ProxyOutcome.Banned)]
    [InlineData((HttpStatusCode)429, ProxyOutcome.Banned)]
    // The proxy itself rejected us.
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, ProxyOutcome.Failure)]
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

    [Fact]
    public void FromException_Should_Return_Failure_For_An_Unmapped_Exception_Type()
    {
        // Any exception type not explicitly handled falls to the default case: Failure.
        var exception = new InvalidOperationException("unknown error");

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

    [Fact]
    public void FromResponse_Should_Respect_The_Inspect_Override_Over_Status_Code()
    {
        // The most valuable signal a scraper has: a portal returning HTTP 200 with a captcha page.
        // The custom inspector wins over the generic status-code rule.
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        var result = ProxyOutcomeClassifier.FromResponse(response, _ => ProxyOutcome.Banned);

        result.ShouldBe(ProxyOutcome.Banned);
    }

    [Fact]
    public void FromResponse_Should_Fall_Through_To_Status_Code_When_Inspect_Returns_Null()
    {
        // The inspector can opt out by returning null, falling back to the generic rule.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var result = ProxyOutcomeClassifier.FromResponse(response, _ => null);

        result.ShouldBe(ProxyOutcome.Banned);
    }

    [Fact]
    public async Task FromException_Should_Classify_WebException_ProtocolError_With_Status_Code_From_HttpWebResponse()
    {
        // Critical for .NET Framework 4.8 scrapers: when a portal returns 403 over WebRequest,
        // it surfaces as a WebException with ProtocolError status and an HttpWebResponse inner.
        // If misclassified as Failure, the policy engine would prescribe the wrong remedy.

        // Find a free port using TcpListener
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var freePort = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{freePort}/");

        try
        {
            listener.Start();
            var uri = new Uri($"http://localhost:{freePort}/test");

            var listenerTask = Task.Run(async () =>
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                }
                catch (ObjectDisposedException)
                {
                    // Listener was closed before request came in, which is fine for the test.
                }
            });

            WebException? caughtException = null;
            try
            {
#pragma warning disable SYSLIB0014
                var request = (HttpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
                request.Timeout = 5000;

                try
                {
                    using var response = await request.GetResponseAsync();
                }
                catch (WebException ex)
                {
                    caughtException = ex;
                }
            }
            finally
            {
                try
                {
                    await listenerTask;
                }
                catch (TaskCanceledException)
                {
                    // Task was cancelled, which is expected if the listener is stopped.
                }
            }

            caughtException.ShouldNotBeNull();
            caughtException.Status.ShouldBe(WebExceptionStatus.ProtocolError);
            caughtException.Response.ShouldNotBeNull();

            var result = ProxyOutcomeClassifier.FromException(caughtException);
            result.ShouldBe(ProxyOutcome.Banned);
        }
        finally
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, which is fine.
            }
        }
    }
}
