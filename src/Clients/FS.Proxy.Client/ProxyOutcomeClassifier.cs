using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace FSH.Proxy.Client;

/// <summary>
/// Decides, for one failed or completed attempt, whether the PROXY failed or the DESTINATION failed.
/// </summary>
/// <remarks>
/// This is the reason the SDK exists as a shared package rather than a snippet in each scraper.
/// The service's policy engine disables a proxy once enough negative outcomes accumulate from
/// enough distinct reporters; if one scraper reports a portal's 503 as <see cref="ProxyOutcome.Failure"/>
/// and another reports it as <see cref="ProxyOutcome.Success"/>, the engine acts on noise — and the
/// failure mode is the expensive direction: a portal having a bad afternoon takes the whole pool
/// down with it.
///
/// The rule in one line: if the destination answered at all, the tunnel worked.
/// </remarks>
public static class ProxyOutcomeClassifier
{
    /// <summary>Classifies a response that actually arrived, by its status code.</summary>
    public static ProxyOutcome FromStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        // The destination recognized and rejected the proxy's IP. A different proxy may work.
        HttpStatusCode.Forbidden => ProxyOutcome.Banned,
        (HttpStatusCode)429 => ProxyOutcome.Banned,

        // The PROXY rejected us — it wanted credentials we did not supply or that were wrong.
        HttpStatusCode.ProxyAuthenticationRequired => ProxyOutcome.Failure,

        // Everything else — including 408 and 504 — means the destination answered, so the tunnel
        // worked; the proxy did its job. 408 is the ORIGIN server's own response (it decided to
        // declare a client-side idle timeout — nothing to do with the proxy in front of it); 504 is
        // the destination's own gateway/reverse-proxy answering with an error status, exactly like
        // 502/503. Deliberately NOT ProxyOutcome.Timeout despite the name: an overloaded origin
        // emits 502/503/504 near-interchangeably, and treating 504 differently from its siblings
        // would report the same bad afternoon as Success via one status and Timeout via another,
        // quarantining every proxy that happens to touch it. See the design spec's §3
        // classification table, which lists both explicitly so this reads as an intentional
        // decision rather than an unremarked deviation.
        _ => ProxyOutcome.Success,
    };

    /// <summary>Classifies a response, optionally consulting a caller-supplied content inspector first.</summary>
    /// <param name="response">The response to classify.</param>
    /// <param name="inspect">
    /// Optional. Lets the caller recognize a soft block — a destination returning HTTP 200 with a
    /// captcha or robot-check page. No generic rule can detect that, and it is the single most
    /// valuable signal a scraper has. Return <c>null</c> to fall through to the status code.
    /// </param>
    public static ProxyOutcome FromResponse(HttpResponseMessage response, Func<HttpResponseMessage, ProxyOutcome?>? inspect = null)
    {
#if NET
        ArgumentNullException.ThrowIfNull(response);
#else
#pragma warning disable CA1510
        if (response is null) throw new ArgumentNullException(nameof(response));
#pragma warning restore CA1510
#endif

        var inspected = inspect?.Invoke(response);
        return inspected ?? FromStatusCode(response.StatusCode);
    }

    /// <summary>Classifies a thrown exception.</summary>
    public static ProxyOutcome FromException(Exception exception)
    {
#if NET
        ArgumentNullException.ThrowIfNull(exception);
#else
#pragma warning disable CA1510
        if (exception is null) throw new ArgumentNullException(nameof(exception));
#pragma warning restore CA1510
#endif

        switch (exception)
        {
            // HttpClient's timeout surfaces as a cancelled task whose inner is a TimeoutException.
            // A cancellation WITHOUT that inner is ambiguous from inside this method alone: it can
            // be the caller's own shutdown (not the proxy's fault) or a genuine, proxy-unrelated
            // cancellation. This classifier has no access to the CancellationToken that triggered
            // it, so it cannot make that distinction here and falls back to Failure as a
            // conservative default. A caller that DOES hold the token (e.g.
            // FsProxyRotationHandler.SendAsync) must check token.IsCancellationRequested itself and
            // skip calling Report entirely for its own cancellation, rather than rely on this method
            // to recognize it — see that method's own remarks.
#if !NET
            // netstandard2.0 (.NET Framework) divergence: HttpClient's timeout throws
            // TaskCanceledException with NO inner TimeoutException on this target — that inner was
            // only added by the BCL in .NET 5. Every HttpClient timeout on netstandard2.0 therefore
            // falls into this same branch and classifies as Failure below, never Timeout. This is a
            // known, accepted gap for this target, not something to paper over here: guessing (e.g.
            // treating every parameterless cancellation as a timeout) would trade a known miss for a
            // wrong guess elsewhere, which is worse. Tracked in the design spec's follow-ups.
#endif
            case TaskCanceledException taskCanceled:
                return taskCanceled.InnerException is TimeoutException ? ProxyOutcome.Timeout : ProxyOutcome.Failure;

            case TimeoutException:
                return ProxyOutcome.Timeout;

            // Carries a status code from .NET 5 onward; on netstandard2.0 StatusCode is always null,
            // so this falls through to Failure and the caller should prefer FromResponse there.
#if NET
            case HttpRequestException httpRequest when httpRequest.StatusCode.HasValue:
                return FromStatusCode(httpRequest.StatusCode.Value);
#endif

            // The 4.8 scrapers are on WebRequest and see WebException, not HttpRequestException.
            case WebException webException:
                return FromWebException(webException);

            default:
                return ProxyOutcome.Failure;
        }
    }

    private static ProxyOutcome FromWebException(WebException exception)
    {
        if (exception.Status == WebExceptionStatus.Timeout)
        {
            return ProxyOutcome.Timeout;
        }

        // ProtocolError means the destination answered with an error status — the status is the
        // real signal, not the exception. A 403 here is Banned, not a broken proxy.
        if (exception.Status == WebExceptionStatus.ProtocolError && exception.Response is HttpWebResponse response)
        {
            return FromStatusCode(response.StatusCode);
        }

        return ProxyOutcome.Failure;
    }
}
