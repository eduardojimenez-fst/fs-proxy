using System.Net.Http;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Caching;
using FSH.Proxy.Client.DependencyInjection;
using FSH.Proxy.Client.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.DependencyInjection;

/// <summary>
/// A broken DI registration in <c>ServiceCollectionExtensions</c> would previously fail only inside a
/// real consumer's app, the first time it actually resolved <see cref="IProxySource"/> or sent a
/// request — these tests catch that here instead, with a real <see cref="ServiceCollection"/> and no
/// mocking of the DI container itself.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BaseAddress"] = "https://proxy.test",
                ["ApiKey"] = "fsh_proxies_test",
            })
            .Build();

    [Fact]
    public async Task AddFsProxyClient_Should_Register_A_Resolvable_IProxySource()
    {
        var services = new ServiceCollection();

        services.AddFsProxyClient(Configuration());
        await using ServiceProvider provider = services.BuildServiceProvider();

        IProxySource source = provider.GetRequiredService<IProxySource>();

        source.ShouldNotBeNull();
    }

    [Fact]
    public async Task AddFsProxyRotation_Should_Put_FsProxyRotationHandler_In_The_Clients_Pipeline()
    {
        var services = new ServiceCollection();
        services.AddFsProxyClient(Configuration());
        services.AddHttpClient("scraper").AddFsProxyRotation("country:cl");
        await using ServiceProvider provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("scraper");

        bool foundRotationHandler = false;
        HttpMessageHandler? current = handler;
        while (current is DelegatingHandler delegating)
        {
            if (delegating is FsProxyRotationHandler)
            {
                foundRotationHandler = true;
                break;
            }

            current = delegating.InnerHandler;
        }

        foundRotationHandler.ShouldBeTrue();
    }

    // I12: SnapshotCache is an IProxySnapshotCache — not bindable from IConfiguration — so this
    // overload was the only Level-2 DI path that could ever reach it at all. Without it, the
    // startup-outage protection the cache exists to provide was unreachable from the DI-based
    // adoption path, even though it worked perfectly well when ProxyClientOptions was built by hand.
    [Fact]
    public async Task AddFsProxyClient_With_ConfigureOptions_Should_Apply_The_Configured_SnapshotCache()
    {
        var services = new ServiceCollection();
        var cache = new FileSnapshotCache(
            Path.Combine(Path.GetTempPath(), "fsproxy-di-test-" + Guid.NewGuid().ToString("N")),
            TimeSpan.FromHours(24));

        services.AddFsProxyClient(Configuration(), options => options.SnapshotCache = cache);
        await using ServiceProvider provider = services.BuildServiceProvider();

        var registeredOptions = provider.GetRequiredService<ProxyClientOptions>();

        registeredOptions.SnapshotCache.ShouldBeSameAs(cache);
    }

    // Companion: configureOptions still runs through the same Validate() as the plain overload —
    // a caller misusing it to set an out-of-range value must fail loudly at startup, not silently.
    [Fact]
    public void AddFsProxyClient_With_ConfigureOptions_Should_Still_Validate_The_Result()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() =>
            services.AddFsProxyClient(Configuration(), options => options.PoolSize = 0));
    }
}
