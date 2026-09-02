using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Inbox;
using FSH.Framework.Eventing.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Guards the defect behind issue #1349: IOutboxStore/IInboxStore were registered
/// once per module DbContext, non-keyed, so .NET DI silently resolved whichever
/// module registered last — for the whole application, including Identity.
/// </summary>
public class EventingRegistrationTests
{
    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseOptions:Provider"] = "postgresql",
                ["DatabaseOptions:ConnectionString"] = "Host=arch;Database=arch;Username=arch;Password=arch",
                ["DatabaseOptions:MigrationsAssembly"] = "FS.Proxy.Migrations.PostgreSQL",
            })
            .Build();
    }

    private static IServiceCollection BuildServices()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddEventingCore(BuildConfiguration());
        return services;
    }

    [Fact]
    public void AddEventingCore_Registers_Exactly_One_OutboxStore()
    {
        BuildServices()
            .Count(d => d.ServiceType == typeof(IOutboxStore))
            .ShouldBe(1, "a second IOutboxStore registration silently hijacks every module's outbox (issue #1349)");
    }

    [Fact]
    public void AddEventingCore_Registers_Exactly_One_InboxStore()
    {
        BuildServices()
            .Count(d => d.ServiceType == typeof(IInboxStore))
            .ShouldBe(1, "a second IInboxStore registration silently redirects idempotency writes (issue #1349)");
    }

    [Fact]
    public void AddEventingCore_Is_Idempotent()
    {
        IConfiguration configuration = BuildConfiguration();
        IServiceCollection services = new ServiceCollection();
        services.AddEventingCore(configuration);
        services.AddEventingCore(configuration);

        services.Count(d => d.ServiceType == typeof(IOutboxStore))
            .ShouldBe(1, "calling AddEventingCore twice must not multiply the IOutboxStore registration — that would reintroduce the exact ambiguity issue #1349 is about");
        services.Count(d => d.ServiceType == typeof(IInboxStore))
            .ShouldBe(1, "calling AddEventingCore twice must not multiply the IInboxStore registration — that would reintroduce the exact ambiguity issue #1349 is about");
    }

    [Fact]
    public void AddEventingForDbContext_No_Longer_Exists()
    {
        typeof(ServiceCollectionExtensions)
            .GetMethods()
            .Any(m => m.Name == "AddEventingForDbContext")
            .ShouldBeFalse("per-DbContext outbox registration is the #1349 footgun; the framework owns one EventingDbContext");
    }
}
