using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace FSH.Proxy.Client.Transport;

// Plain classes with a single public constructor, not records: records' compiler-generated `init`
// accessors need System.Runtime.CompilerServices.IsExternalInit, which does not exist on
// netstandard2.0 and is not auto-supplied here (unlike some other TFM/compiler combinations). A
// single public constructor is still all System.Text.Json needs to deserialize into these types.

/// <summary>
/// Wire shape of one item in the <c>POST /api/v1/proxies/request</c> response array. Mirrors the
/// service's <c>ProxyConnectionDto</c> field-for-field; <see cref="Protocol"/> is carried only
/// because the response includes it — the client has no use for it, so nothing maps it onward.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Only instantiated by System.Text.Json via reflection when deserializing the " +
        "/request response body — never with 'new', so the analyzer cannot see a use.")]
internal sealed class ProxyConnectionWireDto
{
    public ProxyConnectionWireDto(Guid id, string host, int port, string? protocol, string? username, string? password)
    {
        Id = id;
        Host = host;
        Port = port;
        Protocol = protocol;
        Username = username;
        Password = password;
    }

    public Guid Id { get; }
    public string Host { get; }
    public int Port { get; }
    public string? Protocol { get; }
    public string? Username { get; }
    public string? Password { get; }
}

/// <summary>
/// Wire shape of the <c>POST /api/v1/proxies/request</c> request body. Mirrors the service's
/// internal <c>RequestProxiesEndpoint.RequestProxiesBody</c>. <see cref="Strategy"/> is a plain
/// string, not the server's <c>ProxySelectionStrategy</c> enum, so this project never has to
/// reference the Proxies module to build its own request.
/// </summary>
internal sealed class RequestProxiesWireRequest
{
    public RequestProxiesWireRequest(IReadOnlyList<string> tags, int count, string strategy, string? sessionId)
    {
        Tags = tags;
        Count = count;
        Strategy = strategy;
        SessionId = sessionId;
    }

    public IReadOnlyList<string> Tags { get; }
    public int Count { get; }
    public string Strategy { get; }
    public string? SessionId { get; }
}

/// <summary>Wire shape of one event inside a feedback batch. Mirrors the service's <c>ProxyFeedbackEvent</c>.</summary>
internal sealed class ProxyFeedbackWireEvent
{
    public ProxyFeedbackWireEvent(Guid proxyId, ProxyOutcome outcome, string? detail)
    {
        ProxyId = proxyId;
        Outcome = outcome;
        Detail = detail;
    }

    public Guid ProxyId { get; }
    public ProxyOutcome Outcome { get; }
    public string? Detail { get; }
}

/// <summary>
/// Wire shape of the <c>POST /api/v1/proxies/feedback/batch</c> request body. Mirrors the service's
/// internal <c>ReportProxyFeedbackBatchEndpoint.ReportProxyFeedbackBatchBody</c>.
/// </summary>
internal sealed class ReportProxyFeedbackBatchWireRequest
{
    public ReportProxyFeedbackBatchWireRequest(IReadOnlyList<ProxyFeedbackWireEvent> events)
    {
        Events = events;
    }

    public IReadOnlyList<ProxyFeedbackWireEvent> Events { get; }
}
