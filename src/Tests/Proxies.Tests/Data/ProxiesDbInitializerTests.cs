using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Data;

public sealed class ProxiesDbInitializerTests
{
    private static readonly Dictionary<string, string?> BothProvidersConfigured = new()
    {
        ["Seed:ProxyProviders:BrightData:ApiToken"] = "token-123",
        ["Seed:ProxyProviders:BrightData:Zone"] = "datacenter_zone",
        ["Seed:ProxyProviders:WebShare:ApiKey"] = "webshare-key-123",
    };

    private static readonly Dictionary<string, string?> OnlyBrightDataConfigured = new()
    {
        ["Seed:ProxyProviders:BrightData:ApiToken"] = "token-123",
        ["Seed:ProxyProviders:BrightData:Zone"] = "datacenter_zone",
    };

    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProxiesDbInitializer CreateSut(
        ProxiesDbContext db, Dictionary<string, string?> config, bool isDevelopment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(isDevelopment ? Environments.Development : Environments.Production);
        return new ProxiesDbInitializer(
            db, configuration, environment, new FakeSecretProtector(), NullLogger<ProxiesDbInitializer>.Instance);
    }

    [Fact]
    public async Task SeedAsync_Should_SeedManualAccount()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, [], isDevelopment: false);

        await sut.SeedAsync(CancellationToken.None);

        (await db.ProviderAccounts.AnyAsync(x => x.Id == ManualProviderAccount.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task SeedAsync_Should_NotSeedDevProviderAccounts_When_NotDevelopment()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, BothProvidersConfigured, isDevelopment: false);

        await sut.SeedAsync(CancellationToken.None);

        (await db.ProviderAccounts.CountAsync()).ShouldBe(1); // only the Manual account
    }

    [Fact]
    public async Task SeedAsync_Should_SeedOnlyConfiguredProvider_When_Development()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, OnlyBrightDataConfigured, isDevelopment: true);

        await sut.SeedAsync(CancellationToken.None);

        (await db.ProviderAccounts.AnyAsync(x => x.Name == "BrightData (dev seed)")).ShouldBeTrue();
        (await db.ProviderAccounts.AnyAsync(x => x.Name == "WebShare (dev seed)")).ShouldBeFalse();
    }

    [Fact]
    public async Task SeedAsync_Should_SeedBothProviderAccounts_When_BothConfigured()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, BothProvidersConfigured, isDevelopment: true);

        await sut.SeedAsync(CancellationToken.None);

        (await db.ProviderAccounts.AnyAsync(x => x.Name == "BrightData (dev seed)")).ShouldBeTrue();
        (await db.ProviderAccounts.AnyAsync(x => x.Name == "WebShare (dev seed)")).ShouldBeTrue();
    }

    [Fact]
    public async Task SeedAsync_Should_NotDuplicate_When_RunTwice()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, BothProvidersConfigured, isDevelopment: true);

        await sut.SeedAsync(CancellationToken.None);
        await sut.SeedAsync(CancellationToken.None);

        (await db.ProviderAccounts.CountAsync(x => x.Name == "BrightData (dev seed)")).ShouldBe(1);
        (await db.ProviderAccounts.CountAsync(x => x.Name == "WebShare (dev seed)")).ShouldBe(1);
        (await db.ProviderAccounts.CountAsync(x => x.Id == ManualProviderAccount.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task SeedAsync_Should_SeedFiveTagCategories_With_CorrectValueCounts()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, [], isDevelopment: false);

        await sut.SeedAsync(CancellationToken.None);

        var categories = await db.TagCategories.Include(x => x.Values).ToListAsync();
        categories.Count.ShouldBe(5);
        categories.Single(x => x.Name == "country").Values.Count.ShouldBe(9);
        categories.Single(x => x.Name == "source").Values.Count.ShouldBe(21);
        categories.Single(x => x.Name == "entityType").Values.Count.ShouldBe(13);
        categories.Single(x => x.Name == "operationType").Values.Count.ShouldBe(1);
        categories.Single(x => x.Name == "application").Values.Count.ShouldBe(8);
    }

    [Fact]
    public async Task SeedAsync_Should_NotDuplicateTagCategories_When_RunTwice()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, [], isDevelopment: false);

        await sut.SeedAsync(CancellationToken.None);
        await sut.SeedAsync(CancellationToken.None);

        (await db.TagCategories.CountAsync()).ShouldBe(5);
    }

    private sealed class FakeSecretProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string ciphertext) => ciphertext.Replace("protected:", string.Empty, StringComparison.Ordinal);
    }
}
