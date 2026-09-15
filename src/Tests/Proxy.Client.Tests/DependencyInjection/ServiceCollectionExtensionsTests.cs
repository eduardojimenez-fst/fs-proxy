using System.Net.Http;
using FSH.Proxy.Client;
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
}
