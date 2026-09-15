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

        // Something upstream timed out. Treated as a proxy-side stall.
        HttpStatusCode.RequestTimeout => ProxyOutcome.Timeout,
        HttpStatusCode.GatewayTimeout => ProxyOutcome.Timeout,

        // Everything else — 2xx, 3xx, 404, 400, 500, 502, 503 — means the destination answered.
        // The proxy did its job; the site's own problems are not the proxy's fault.
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
            // A cancellation WITHOUT that inner is our own shutdown — never blame a proxy for it.
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
