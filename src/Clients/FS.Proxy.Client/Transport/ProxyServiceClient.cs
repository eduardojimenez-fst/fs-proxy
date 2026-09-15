using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FSH.Proxy.Client.Transport;

/// <summary>
/// The only type in the SDK that speaks HTTP to the Proxy Management Service. Everything else in
/// the package works against <see cref="IProxyServiceClient"/> so it can be faked in tests.
/// </summary>
public sealed class ProxyServiceClient : IProxyServiceClient
{
    /// <summary>
    /// The service's own validator (<c>RequestProxiesQueryValidator</c>) caps <c>Count</c> at 50 and
    /// silently truncates rather than rejecting — clamping here just avoids sending a value the
    /// server would ignore anyway.
    /// </summary>
    private const int MaxRequestCount = 50;

    private const string ApiKeyHeaderName = "X-Api-Key";
    private const string RequestPath = "api/v1/proxies/request";
    private const string FeedbackBatchPath = "api/v1/proxies/feedback/batch";

    // The service registers JsonStringEnumConverter (Program.cs), so enums MUST travel as names —
    // a numeric enum deserializes to the wrong member on the other side with no error. One static
    // instance rather than one per call: CA1869 fails the build otherwise.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _httpClient;
    private readonly ProxyClientOptions _options;

    public ProxyServiceClient(HttpClient httpClient, ProxyClientOptions options)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
#else
#pragma warning disable CA1510
        if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));
        if (options is null) throw new ArgumentNullException(nameof(options));
#pragma warning restore CA1510
#endif

        _httpClient = httpClient;
        _options = options;

        // The caller supplies BaseAddress through ProxyClientOptions, not by pre-configuring the
        // HttpClient — this keeps ProxyClientOptions the single place every knob lives, including
        // for the .NET Framework 4.8 scrapers wiring HttpClient up by hand.
        if (options.BaseAddress is not null)
        {
            _httpClient.BaseAddress = options.BaseAddress;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProxyEndpoint>> RequestAsync(IReadOnlyList<string> tags, int count, CancellationToken ct)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tags);
#else
#pragma warning disable CA1510
        if (tags is null) throw new ArgumentNullException(nameof(tags));
#pragma warning restore CA1510
#endif

        var normalizedTags = tags.Select(ProxyTags.Normalize).ToList();
        int clampedCount = Math.Min(count, MaxRequestCount);

        // Never "RoundRobin": that cursor is global per tag-set on the server and shared by every
        // consumer, so filling a local pool through it turns it into noise for everyone else.
        // Rotation across a pool's own members is this client's job, done locally.
        var requestBody = new RequestProxiesWireRequest(normalizedTags, clampedCount, "Random", sessionId: null);

        using var request = CreateJsonRequest(HttpMethod.Post, RequestPath, requestBody);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

        // A 404 here means "no Active proxy matches these tags" — an ordinary state the caller
        // handles the same way as any other empty result (keep serving the stale snapshot), not an
        // exception. Any other non-success status (e.g. 401 for a bad API key) must still throw.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<ProxyEndpoint>();
        }

        response.EnsureSuccessStatusCode();

        // netstandard2.0's HttpContent has no ReadAsStreamAsync(CancellationToken) overload.
#if NET6_0_OR_GREATER
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var items = await JsonSerializer.DeserializeAsync<List<ProxyConnectionWireDto>>(stream, SerializerOptions, ct).ConfigureAwait(false);

        if (items is null)
        {
            return Array.Empty<ProxyEndpoint>();
        }

        return items.Select(i => new ProxyEndpoint(i.Id, i.Host, i.Port, i.Username, i.Password)).ToList();
    }

    /// <inheritdoc />
    public async Task RequestFeedbackAsync(IReadOnlyList<FeedbackItem> events, CancellationToken ct)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(events);
#else
#pragma warning disable CA1510
        if (events is null) throw new ArgumentNullException(nameof(events));
#pragma warning restore CA1510
#endif

        if (events.Count == 0)
        {
            return;
        }

        var wireEvents = events.Select(e => new ProxyFeedbackWireEvent(e.ProxyId, e.Outcome, e.Detail)).ToList();
        var requestBody = new ReportProxyFeedbackBatchWireRequest(wireEvents);

        using var request = CreateJsonRequest(HttpMethod.Post, FeedbackBatchPath, requestBody);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateJsonRequest<TBody>(HttpMethod method, string relativePath, TBody body)
    {
        string json = JsonSerializer.Serialize(body, SerializerOptions);
        var request = new HttpRequestMessage(method, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        string? apiKey = _options.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add(ApiKeyHeaderName, apiKey);
        }

        return request;
    }
}
