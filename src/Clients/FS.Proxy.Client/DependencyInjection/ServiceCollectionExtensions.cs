#if NET
using System;
using System.Net.Http;
using FSH.Proxy.Client.Http;
using FSH.Proxy.Client.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Proxy.Client.DependencyInjection;

/// <summary>
/// Adoption level 2's DI wiring — net10-only. Everything here is a thin convenience over what a
/// DI-less caller could already build by hand: <see cref="AddFsProxyClient(IServiceCollection, IConfiguration)"/> binds
/// <see cref="ProxyClientOptions"/> from configuration and registers a single, container-owned
/// <see cref="IProxySource"/>; <see cref="AddFsProxyRotation"/> adds <see cref="FsProxyRotationHandler"/>
/// to one named/typed <see cref="HttpClient"/>'s pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The name of the <see cref="HttpClient"/> (registered via <see cref="IHttpClientFactory"/>)
    /// that <see cref="IProxySource"/>'s own transport — <see cref="ProxyServiceClient"/> — uses to
    /// talk to the Proxy Management Service.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="IHttpClientFactory"/> rather than <see cref="ProxySource"/>'s own
    /// self-constructed-<see cref="HttpClient"/> constructor overload — but NOT for the usual "pooled,
    /// recycled handlers" reason <c>IHttpClientFactory</c> is normally reached for. <see cref="IProxySource"/>
    /// is registered as a singleton, so the factory delegate that calls <c>CreateClient(TransportClientName)</c>
    /// runs exactly once, at that singleton's construction, and the resulting <see cref="HttpClient"/> is
    /// then captured and reused for the whole process's lifetime — functionally identical to a plain
    /// <c>new HttpClient()</c> in that specific respect; no per-call handler rotation ever actually
    /// happens here. The real reason to go through <c>IHttpClientFactory</c> anyway: it puts this SDK's
    /// own outbound calls to the Proxy Management Service on the same named-client configuration surface
    /// as every other outbound call in the host, so a consumer can attach logging, a Polly resilience
    /// policy, or a custom primary handler to <see cref="TransportClientName"/> via the ordinary
    /// <c>services.AddHttpClient(TransportClientName).Configure...</c> calls — instead of this transport
    /// being an invisible, unconfigurable <c>HttpClient</c> living entirely outside the DI container's
    /// view.
    /// </remarks>
    private const string TransportClientName = "FsProxyClient.Transport";

    /// <summary>
    /// Binds a <see cref="ProxyClientOptions"/> from <paramref name="section"/> and registers both that
    /// bound instance and a singleton <see cref="IProxySource"/> built from it.
    /// </summary>
    /// <remarks>
    /// The bound <see cref="ProxyClientOptions"/> is registered as a plain singleton instance — NOT as
    /// <c>IOptions&lt;ProxyClientOptions&gt;</c> or an <c>IOptionsMonitor</c>-style reloadable
    /// registration. This is a one-shot bind at startup, matching <see cref="ProxyClientOptions"/>'s own
    /// "constructible by hand" design (see its remarks): a net10 host gets configuration binding as a
    /// convenience, never a requirement the type itself depends on, and there is no live-reload story
    /// here to lose by skipping <c>IOptions</c>.
    /// <para>
    /// The returned <see cref="IProxySource"/> is a <see cref="ProxySource"/> — an
    /// <see cref="IAsyncDisposable"/> — registered through a factory delegate, so the container owns
    /// and disposes it (flushing pending feedback, stopping every refresh timer) when the host shuts
    /// down. Callers wanting <see cref="IProxySource.WarmupAsync"/> run before the first request still
    /// call it themselves (e.g. from a hosted service) — this method only wires the DI graph, it does
    /// not start scraping.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFsProxyClient(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        var options = new ProxyClientOptions();
        section.Bind(options);
        return AddFsProxyClientCore(services, options);
    }

    /// <summary>
    /// Same as <see cref="AddFsProxyClient(IServiceCollection, IConfiguration)"/>, plus
    /// <paramref name="configureOptions"/> run immediately after binding, before
    /// <see cref="ProxyClientOptions.Validate"/>. This is the only Level-2 DI path that can reach
    /// <see cref="ProxyClientOptions.SnapshotCache"/> at all: it is an <c>IProxySnapshotCache</c>
    /// interface, not a POCO shape <see cref="IConfiguration"/> binding can construct, so without this
    /// overload the startup-outage protection that cache exists to provide — the whole reason
    /// <see cref="Caching.FileSnapshotCache"/> exists — was unreachable from configuration binding.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddFsProxyClient(configuration.GetSection("FsProxy"), options =>
    ///     options.SnapshotCache = new FileSnapshotCache(cacheDirectory, TimeSpan.FromHours(24)));
    /// </code>
    /// </example>
    public static IServiceCollection AddFsProxyClient(this IServiceCollection services, IConfiguration section, Action<ProxyClientOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new ProxyClientOptions();
        section.Bind(options);
        configureOptions(options);
        return AddFsProxyClientCore(services, options);
    }

    private static IServiceCollection AddFsProxyClientCore(IServiceCollection services, ProxyClientOptions options)
    {
        options.Validate();

        services.AddHttpClient(TransportClientName);
        services.AddSingleton(options);
        services.AddSingleton<IProxySource>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            IProxyServiceClient client = new ProxyServiceClient(httpClientFactory.CreateClient(TransportClientName), options);
            return new ProxySource(options, client);
        });

        return services;
    }

    /// <summary>
    /// Adds <see cref="FsProxyRotationHandler"/> to this <see cref="HttpClient"/>'s message handler
    /// pipeline, leasing against <paramref name="tags"/> from the container's <see cref="IProxySource"/>
    /// (registered by <see cref="AddFsProxyClient(IServiceCollection, IConfiguration)"/>). See <see cref="FsProxyRotationHandler"/>'s own
    /// remarks for why it terminates the pipeline itself (rather than delegating to whatever primary
    /// handler this <see cref="HttpClient"/> was otherwise configured with) whenever a proxy is
    /// actually leased — a primary handler configured via
    /// <c>ConfigurePrimaryHttpMessageHandler</c> on this same builder is used only for the
    /// no-proxy-available fallback.
    /// </summary>
    public static IHttpClientBuilder AddFsProxyRotation(this IHttpClientBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tags);

        return builder.AddHttpMessageHandler(sp => new FsProxyRotationHandler(sp.GetRequiredService<IProxySource>(), tags));
    }
}
#endif
