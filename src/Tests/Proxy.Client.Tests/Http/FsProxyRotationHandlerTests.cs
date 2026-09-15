using System.Net;
using System.Net.Http;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Http;

public sealed class FsProxyRotationHandlerTests
{
    private static ProxyEndpoint Endpoint() =>
        new(Guid.NewGuid(), "203.0.113.50", 8080, ProxyProtocol.Http, "user", "pass");

    private static IProxySource FakeSourceLeasing(ProxyEndpoint? endpoint)
    {
        var source = Substitute.For<IProxySource>();
        source.Lease(Arg.Any<string[]>()).Returns(endpoint);
        return source;
    }

    /// <summary>Records the exact request instance it received and returns a canned response.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) => _status = status;

        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    /// <summary>Throws a canned exception instead of ever answering — simulates a timed-out send.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exception;
    }

    // 1. The handler sets the request's proxy and sends: prove the exact HttpRequestMessage the
    // caller handed to SendAsync is the one that reaches the handler built FOR THE LEASED PROXY,
    // not some other path — a test that only checked "Lease was called" could pass even if the
    // handler then ignored the result and sent unproxied.
    [Fact]
    public async Task SendAsync_Should_Route_The_Request_Through_A_Handler_Built_For_The_Leased_Proxy()
    {
        ProxyEndpoint proxy = Endpoint();
        var recorder = new RecordingHandler(HttpStatusCode.OK);
        var capturedEndpoints = new List<ProxyEndpoint>();
        IProxySource source = FakeSourceLeasing(proxy);
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: ep =>
            {
                capturedEndpoints.Add(ep);
                return recorder;
            });
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        capturedEndpoints.ShouldHaveSingleItem();
        capturedEndpoints[0].Id.ShouldBe(proxy.Id);
        recorder.LastRequest.ShouldBeSameAs(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // 2. A 200 reports Success.
    [Fact]
    public async Task SendAsync_Should_Report_Success_On_200()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var recorder = new RecordingHandler(HttpStatusCode.OK);
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ => recorder);
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        await invoker.SendAsync(request, CancellationToken.None);

        source.Received(1).Report(proxy.Id, ProxyOutcome.Success, Arg.Any<string?>());
    }

    // 3. A 403 reports Banned.
    [Fact]
    public async Task SendAsync_Should_Report_Banned_On_403()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var recorder = new RecordingHandler(HttpStatusCode.Forbidden);
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ => recorder);
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        await invoker.SendAsync(request, CancellationToken.None);

        source.Received(1).Report(proxy.Id, ProxyOutcome.Banned, Arg.Any<string?>());
    }

    // 4. A 503 reports Success — the proxy worked, the portal did not.
    [Fact]
    public async Task SendAsync_Should_Report_Success_On_503_Because_The_Proxy_Itself_Worked()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var recorder = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ => recorder);
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        await invoker.SendAsync(request, CancellationToken.None);

        source.Received(1).Report(proxy.Id, ProxyOutcome.Success, Arg.Any<string?>());
    }

    // 5. A thrown TaskCanceledException with a TimeoutException inner reports Timeout AND rethrows —
    // the handler observes, it does not change control flow.
    [Fact]
    public async Task SendAsync_Should_Report_Timeout_And_Rethrow_When_The_Send_Times_Out()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var timeoutException = new TaskCanceledException("timed out", new TimeoutException());
        var thrower = new ThrowingHandler(timeoutException);
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ => thrower);
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        // Asserting the SAME exception INSTANCE re-emerges is not possible here, and is not what
        // "does not change control flow" means: `SendAsync` is itself an async Task-returning method,
        // and the CLR's AsyncTaskMethodBuilder converts any OperationCanceledException/
        // TaskCanceledException escaping an async method into a Canceled-state Task — awaiting that
        // always hands the caller a FRESH exception object, never the original one. This is not
        // something this handler's `throw;` can avoid: it is the exact same mechanism that makes a
        // real HttpClient timeout arrive at ITS caller as a distinct TaskCanceledException(TimeoutException)
        // instance too (see ProxyOutcomeClassifier's own remarks on that). What this test can and does
        // prove: the exception is neither swallowed (ThrowAsync succeeds) nor reclassified into some
        // other exception type, and the ORIGINAL exception — inner TimeoutException intact — was the
        // one actually classified and reported, which is only true if Report ran BEFORE the rethrow
        // triggered that conversion.
        await Should.ThrowAsync<TaskCanceledException>(() => invoker.SendAsync(request, CancellationToken.None));

        source.Received(1).Report(proxy.Id, ProxyOutcome.Timeout, "timed out");
    }

    // 6. A supplied classifyResponse returning Banned on a 200 wins over the status code — the
    // captcha-page case.
    [Fact]
    public async Task SendAsync_Should_Let_ClassifyResponse_Override_A_200_As_Banned()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var recorder = new RecordingHandler(HttpStatusCode.OK);
        using var sut = new FsProxyRotationHandler(source, ["country:cl"],
            classifyResponse: _ => ProxyOutcome.Banned,
            perProxyHandlerFactory: _ => recorder);
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        await invoker.SendAsync(request, CancellationToken.None);

        source.Received(1).Report(proxy.Id, ProxyOutcome.Banned, Arg.Any<string?>());
    }

    // 7. With no proxy available, the request is sent directly rather than failing.
    [Fact]
    public async Task SendAsync_Should_Send_Directly_When_No_Proxy_Is_Available()
    {
        IProxySource source = FakeSourceLeasing(null);
        var direct = new RecordingHandler(HttpStatusCode.OK);
        var perProxyFactoryCalls = 0;
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ =>
            {
                perProxyFactoryCalls++;
                throw new InvalidOperationException("Should never build a per-proxy handler with no leased proxy.");
            })
        {
            InnerHandler = direct,
        };
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        direct.LastRequest.ShouldBeSameAs(request);
        direct.CallCount.ShouldBe(1);
        perProxyFactoryCalls.ShouldBe(0);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // No proxy was used, so there is nothing to attribute an outcome to.
        source.DidNotReceive().Report(Arg.Any<Guid>(), Arg.Any<ProxyOutcome>(), Arg.Any<string?>());
    }

    // Bonus: the production (non-test-seam) handler factory actually configures the proxy — the
    // piece the routing test above stubs out. Without this, every test above could pass while the
    // real CreateDefaultPerProxyHandler silently built a handler with no proxy at all.
    [Fact]
    public void CreateDefaultPerProxyHandler_Should_Configure_The_Leased_Proxy_On_The_HttpClientHandler()
    {
        ProxyEndpoint proxy = Endpoint();

        using HttpClientHandler handler = FsProxyRotationHandler.CreateDefaultPerProxyHandler(proxy);

        handler.UseProxy.ShouldBeTrue();
        var webProxy = handler.Proxy.ShouldBeOfType<WebProxy>();
        webProxy.Address.ShouldBe(new Uri("http://203.0.113.50:8080"));
        webProxy.Credentials.ShouldNotBeNull();
    }

    // Bonus: the same proxy leased twice reuses one cached invoker/handler instead of building a
    // fresh TCP/TLS-capable handler (and its connection pool) per request.
    [Fact]
    public async Task SendAsync_Should_Reuse_The_Cached_Handler_For_The_Same_Proxy_Across_Calls()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var recorder = new RecordingHandler(HttpStatusCode.OK);
        var factoryCallCount = 0;
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ =>
            {
                factoryCallCount++;
                return recorder;
            });
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);

        using (var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/one"))
        {
            await invoker.SendAsync(first, CancellationToken.None);
        }
        using (var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/two"))
        {
            await invoker.SendAsync(second, CancellationToken.None);
        }

        factoryCallCount.ShouldBe(1);
        recorder.CallCount.ShouldBe(2);
    }
}
