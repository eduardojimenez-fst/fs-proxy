using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FSH.Proxy.Client.Transport;

/// <summary>
/// Speaks HTTP to the Proxy Management Service. The only type in the package that does — isolated
/// behind this interface so every other type in the SDK can fake it in tests.
/// </summary>
public interface IProxyServiceClient
{
    /// <summary>
    /// Requests up to <paramref name="count"/> proxies matching every tag in <paramref name="tags"/>.
    /// Returns an empty list when the service has no Active proxy for these tags — an ordinary,
    /// expected state, not an error.
    /// </summary>
    Task<IReadOnlyList<ProxyEndpoint>> RequestAsync(IReadOnlyList<string> tags, int count, CancellationToken ct);

    /// <summary>Submits a batch of usage outcomes. A no-op — no call is made — for an empty batch.</summary>
    Task RequestFeedbackAsync(IReadOnlyList<FeedbackItem> events, CancellationToken ct);
}

/// <summary>One buffered usage outcome, ready to submit through <see cref="IProxyServiceClient.RequestFeedbackAsync"/>.</summary>
// A plain class, not a record: records' compiler-generated `init` accessors need
// System.Runtime.CompilerServices.IsExternalInit, which does not exist on netstandard2.0.
public sealed class FeedbackItem
{
    public FeedbackItem(Guid proxyId, ProxyOutcome outcome, string? detail)
    {
        ProxyId = proxyId;
        Outcome = outcome;
        Detail = detail;
    }

    public Guid ProxyId { get; }
    public ProxyOutcome Outcome { get; }
    public string? Detail { get; }
}
