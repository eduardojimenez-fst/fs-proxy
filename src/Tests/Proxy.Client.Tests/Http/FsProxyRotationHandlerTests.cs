using System.Linq;
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
    private static ProxyEndpoint Endpoint(string host = "203.0.113.50") =>
        new(Guid.NewGuid(), host, 8080, ProxyProtocol.Http, "user", "pass");

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
        private readonly HttpContent? _content;

        public RecordingHandler(HttpStatusCode status, HttpContent? content = null)
        {
            _status = status;
            _content = content;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            var response = new HttpResponseMessage(_status);
            if (_content is not null)
            {
                response.Content = _content;
            }
            return Task.FromResult(response);
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

    /// <summary>Tracks whether it was ever disposed — used to prove a response was (or was not) cleaned up.</summary>
    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Records whether it was disposed — used to prove cache eviction actually disposes what it evicts.</summary>
    private sealed class DisposeTrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Returns a caller-controlled, not-yet-completed <see cref="Task{TResult}"/> from SendAsync — lets
    /// a test hold a send "in flight" indefinitely and complete it on demand. Records whether Dispose
    /// was ever called while that task was still incomplete, which is the direct, deterministic proof
    /// that eviction did NOT tear down a handler a request was still using.
    /// </summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _tcs;

        public BlockingHandler(TaskCompletionSource<HttpResponseMessage> tcs) => _tcs = tcs;

        public bool DisposedWhileStillInFlight { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _tcs.Task;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_tcs.Task.IsCompleted)
            {
                DisposedWhileStillInFlight = true;
            }

            base.Dispose(disposing);
        }
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

    // 7. With no proxy available, the request is sent directly rather than failing (DI/IHttpClientFactory
    // path, where InnerHandler is always assigned by AddHttpMessageHandler).
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

    // Fix round 1, Important 1: the standalone usage this SDK also invites —
    // `new HttpClient(new FsProxyRotationHandler(source, tags))` — never assigns InnerHandler at all.
    // Before this fix, falling through to base.SendAsync() there threw
    // InvalidOperationException("The inner handler has not been assigned") for EVERY request whenever
    // no proxy was available: a cold or fully-quarantined pool made every single request fail hard,
    // the exact opposite of behaviour 7's own contract.
    [Fact]
    public async Task SendAsync_Should_Send_Directly_Using_A_Default_Handler_When_No_InnerHandler_Is_Assigned()
    {
        IProxySource source = FakeSourceLeasing(null);
        var direct = new RecordingHandler(HttpStatusCode.OK);
        var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: _ => throw new InvalidOperationException("Should never build a per-proxy handler with no leased proxy."),
            directHandlerFactory: () => direct);
        // Deliberately mirrors the real standalone usage this SDK invites: InnerHandler is NEVER
        // assigned here (HttpClient's constructor does not assign it either — it only owns/disposes
        // the handler it was given).
        using var client = new HttpClient(sut);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        direct.LastRequest.ShouldBeSameAs(request);
        direct.CallCount.ShouldBe(1);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        source.DidNotReceive().Report(Arg.Any<Guid>(), Arg.Any<ProxyOutcome>(), Arg.Any<string?>());
    }

    // Fix round 1, Important 2 — the reviewer's highest-value missing test: every test above leases
    // only one proxy, so nothing proves that two DIFFERENT proxies get two DIFFERENT handlers with
    // outcomes reported against the right id. A regression in the cache key (a constant, the tag set,
    // endpoint.Host instead of endpoint.Id, …) would send requests through the WRONG proxy and report
    // outcomes against the wrong one — poisoning the service's fleet-wide policy engine — while every
    // single-proxy test above kept passing.
    [Fact]
    public async Task SendAsync_Should_Route_Different_Proxies_To_Different_Handlers_And_Report_The_Matching_Id()
    {
        ProxyEndpoint proxyA = Endpoint("203.0.113.10");
        ProxyEndpoint proxyB = Endpoint("203.0.113.20");
        var source = Substitute.For<IProxySource>();
        source.Lease(Arg.Any<string[]>()).Returns(proxyA, proxyB);
        var recorderA = new RecordingHandler(HttpStatusCode.OK);
        var recorderB = new RecordingHandler(HttpStatusCode.Forbidden);
        var capturedEndpointIds = new List<Guid>();
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: ep =>
            {
                capturedEndpointIds.Add(ep.Id);
                return ep.Id == proxyA.Id ? recorderA : recorderB;
            });
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var requestA = new HttpRequestMessage(HttpMethod.Get, "https://example.test/a");
        using var requestB = new HttpRequestMessage(HttpMethod.Get, "https://example.test/b");

        await invoker.SendAsync(requestA, CancellationToken.None);
        await invoker.SendAsync(requestB, CancellationToken.None);

        capturedEndpointIds.Count.ShouldBe(2);
        capturedEndpointIds[0].ShouldBe(proxyA.Id);
        capturedEndpointIds[1].ShouldBe(proxyB.Id);
        recorderA.LastRequest.ShouldBeSameAs(requestA);
        recorderA.CallCount.ShouldBe(1);
        recorderB.LastRequest.ShouldBeSameAs(requestB);
        recorderB.CallCount.ShouldBe(1);
        source.Received(1).Report(proxyA.Id, ProxyOutcome.Success, Arg.Any<string?>());
        source.Received(1).Report(proxyB.Id, ProxyOutcome.Banned, Arg.Any<string?>());
    }

    // Fix round 1, Important 3: a bug in a caller-supplied classifyResponse (e.g. a
    // NullReferenceException walking the response body while looking for a captcha) must never be
    // misreported as a fault of the proxy that just delivered a perfectly good response — that is the
    // exact "one scraper reports wrong, the fleet-wide policy engine acts on noise" failure this SDK
    // exists to prevent, here caused by the CALLER's own bug rather than the network. The response
    // must also not leak: since it will never reach the caller (the classifier's exception propagates
    // instead), it is disposed first.
    [Fact]
    public async Task SendAsync_Should_Not_Attribute_A_Throwing_ClassifyResponse_To_The_Proxy_And_Should_Dispose_The_Response()
    {
        ProxyEndpoint proxy = Endpoint();
        IProxySource source = FakeSourceLeasing(proxy);
        var trackingStream = new TrackingStream();
        var recorder = new RecordingHandler(HttpStatusCode.OK, new StreamContent(trackingStream));
        using var sut = new FsProxyRotationHandler(source, ["country:cl"],
            classifyResponse: _ => throw new InvalidOperationException("the caller's captcha detector is broken"),
            perProxyHandlerFactory: _ => recorder);
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/page");

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(request, CancellationToken.None));

        trackingStream.Disposed.ShouldBeTrue();
        // Not merely "not reported as Failure" — not reported AT ALL. The proxy did nothing wrong, so
        // nothing about this attempt belongs in its record, positive or negative.
        source.DidNotReceive().Report(Arg.Any<Guid>(), Arg.Any<ProxyOutcome>(), Arg.Any<string?>());
    }

    // Bonus: the production (non-test-seam) handler factory actually configures the proxy — the
    // piece the routing test above stubs out. Without this, every test above could pass while the
    // real CreateDefaultPerProxyHandler silently built a handler with no proxy at all. Also pins the
    // fix-round addition of a pooled-connection lifetime (Important 4) and asserts the actual
    // credentials, not merely that some credentials object is present (a wrong username/password would
    // previously still pass a bare `ShouldNotBeNull()`).
    [Fact]
    public void CreateDefaultPerProxyHandler_Should_Configure_The_Leased_Proxy_And_A_Connection_Lifetime()
    {
        ProxyEndpoint proxy = Endpoint();

        using HttpMessageHandler handler = FsProxyRotationHandler.CreateDefaultPerProxyHandler(proxy);

        var socketsHandler = handler.ShouldBeOfType<SocketsHttpHandler>();
        socketsHandler.UseProxy.ShouldBeTrue();
        var webProxy = socketsHandler.Proxy.ShouldBeOfType<WebProxy>();
        webProxy.Address.ShouldBe(new Uri("http://203.0.113.50:8080"));
        NetworkCredential expectedCredential = proxy.ToCredential();
        var actualCredential = webProxy.Credentials.ShouldBeOfType<NetworkCredential>();
        actualCredential.UserName.ShouldBe(expectedCredential.UserName);
        actualCredential.Password.ShouldBe(expectedCredential.Password);
        socketsHandler.PooledConnectionLifetime.ShouldBeGreaterThan(TimeSpan.Zero);
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

    // Fix round 1, Important 4 (bound + eviction): without a cap, a long-lived handler accumulates one
    // low-level handler per distinct proxy ever leased, for the whole process lifetime. Leases
    // MaxCachedInvokers + 1 distinct proxies and proves the oldest (least-recently-used) one gets
    // disposed once the cap is exceeded, while the newest stays live.
    [Fact]
    public async Task SendAsync_Should_Evict_And_Dispose_The_Least_Recently_Used_Handler_Once_Over_Capacity()
    {
        int capacity = FsProxyRotationHandler.MaxCachedInvokers;
        var endpoints = new List<ProxyEndpoint>();
        for (int i = 0; i < capacity + 1; i++)
        {
            endpoints.Add(Endpoint("203.0.113." + (i % 250)));
        }

        var source = Substitute.For<IProxySource>();
        source.Lease(Arg.Any<string[]>()).Returns(endpoints[0], endpoints.Skip(1).ToArray());

        var handlersByEndpointId = new Dictionary<Guid, DisposeTrackingHandler>();
        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: ep =>
            {
                var handler = new DisposeTrackingHandler();
                handlersByEndpointId[ep.Id] = handler;
                return handler;
            });
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);

        foreach (ProxyEndpoint _ in endpoints)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/x");
            await invoker.SendAsync(request, CancellationToken.None);
        }

        handlersByEndpointId.Count.ShouldBe(capacity + 1);
        handlersByEndpointId[endpoints[0].Id].Disposed.ShouldBeTrue();
        handlersByEndpointId[endpoints[^1].Id].Disposed.ShouldBeFalse();
    }

    // Fix round 2: the eviction test above is sequential — every send completes before the next one
    // starts — so it cannot catch the actual bug reported against fix round 1: eviction disposing a
    // handler a request is STILL sending through. This test overlaps them for real: starts a send that
    // blocks on a TaskCompletionSource this test controls, leases enough distinct proxies while that
    // send is still pending to push the blocked proxy's entry out as the least-recently-used eviction
    // victim, then completes the blocked send and asserts it still succeeded — and, the direct proof,
    // that its handler was never disposed while the send was still in flight.
    [Fact]
    public async Task SendAsync_Should_Not_Dispose_An_In_Flight_Handler_Even_Under_Eviction_Pressure()
    {
        int capacity = FsProxyRotationHandler.MaxCachedInvokers;
        ProxyEndpoint blockingProxy = Endpoint("203.0.113.200");
        var blockingTcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingHandler = new BlockingHandler(blockingTcs);

        var fillerEndpoints = new List<ProxyEndpoint>();
        for (int i = 0; i < capacity; i++)
        {
            fillerEndpoints.Add(Endpoint("203.0.113." + (100 + (i % 100))));
        }

        var source = Substitute.For<IProxySource>();
        source.Lease(Arg.Any<string[]>()).Returns(blockingProxy, fillerEndpoints.ToArray());

        using var sut = new FsProxyRotationHandler(source, ["country:cl"], classifyResponse: null,
            perProxyHandlerFactory: ep => ep.Id == blockingProxy.Id
                ? blockingHandler
                : new RecordingHandler(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(sut, disposeHandler: false);

        // Starts the send but does not await it: FsProxyRotationHandler.SendAsync runs synchronously
        // (Lease, cache lookup, entering the per-proxy invoker's SendAsync) right up until it awaits
        // blockingTcs.Task, which is not yet completed — so by the time this line returns, the blocking
        // proxy's cache entry exists and is claimed (in-flight) exactly as it would be for a real,
        // slow-to-answer portal.
        using var blockingRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow");
        // CA2025 flags "started but not immediately awaited" as a general disposal-ordering risk — but
        // that is the entire point of this test: it deliberately starts the send and awaits it later
        // (after blockingTcs.SetResult below), by which point blockingRequest is still alive (its
        // `using` does not end until the test method returns).
#pragma warning disable CA2025
        Task<HttpResponseMessage> blockingSend = invoker.SendAsync(blockingRequest, CancellationToken.None);
#pragma warning restore CA2025

        // Leases and sends through `capacity` MORE distinct proxies while the blocking send is still
        // pending — enough to push the cache from 1 (the blocking entry) to capacity + 1, forcing
        // eviction to pick the blocking entry (the only one never touched again, hence the global
        // least-recently-used) as its victim, while it is still in flight.
        foreach (ProxyEndpoint _ in fillerEndpoints)
        {
            using var fillerRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/filler");
            await invoker.SendAsync(fillerRequest, CancellationToken.None);
        }

        blockingTcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        HttpResponseMessage blockingResponse = await blockingSend;

        blockingResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        blockingHandler.DisposedWhileStillInFlight.ShouldBeFalse();
        // The proxy that answered slowly still answered correctly — it must be reported as Success,
        // never as a Failure manufactured by this handler's own eviction disposing its transport out
        // from under it.
        source.Received(1).Report(blockingProxy.Id, ProxyOutcome.Success, Arg.Any<string?>());
        source.DidNotReceive().Report(blockingProxy.Id, ProxyOutcome.Failure, Arg.Any<string?>());
    }
}
