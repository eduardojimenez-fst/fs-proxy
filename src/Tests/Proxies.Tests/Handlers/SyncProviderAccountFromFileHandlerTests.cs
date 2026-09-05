using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;
using FSH.Modules.Proxies.Providers.FileImport;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class SyncProviderAccountFromFileHandlerTests
{
    private const string Header = "Host,Port,Protocol,Username,Password,Geolocation,ProxyKind";

    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private static SyncProviderAccountFromFileCommandHandler CreateSut(ProxiesDbContext db, IProxySecretProtector protector) =>
        new(db, protector, new ProviderAccountSyncService(db, Substitute.For<FSH.Modules.Proxies.Providers.IProxyProviderAdapterFactory>(),
            protector, Substitute.For<FSH.Framework.Eventing.Abstractions.IOutboxWriter>()));

    [Fact]
    public async Task Handle_Should_ImportRowsWithTheirOwnCredentials()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\n89.249.195.245,7000,Http,jgwcycpg,ytz1gdtc8ymc,,";
        var sut = CreateSut(db, new FakeProtector());

        var result = await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Errors.ShouldBeEmpty();
        var proxy = await db.Proxies.SingleAsync();
        proxy.Username.ShouldBe("jgwcycpg");
    }

    [Fact]
    public async Task Handle_Should_PersistAndApplyDefaultCredentials_When_RowsOmitThem()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs - file", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,,,CL,DataCenter";
        var sut = CreateSut(db, new FakeProtector());

        var result = await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, "acct-user", "acct-pass", null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        var proxy = await db.Proxies.SingleAsync();
        proxy.Username.ShouldBe("acct-user");
        proxy.ProtectedPassword.ShouldBe("acct-pass");
        var stored = await db.ProviderAccounts.SingleAsync();
        var storedDefaults = JsonSerializer.Deserialize<FileImportDefaultCredentials>(stored.ProtectedCredentials)!;
        storedDefaults.Username.ShouldBe("acct-user");
    }

    [Fact]
    public async Task Handle_Should_ApplyDefaultGeolocationAndKind_When_RowsOmitThem()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData - file", ProxyProviderType.BrightData, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\nbrd.superproxy.io,44445,Http,u,p,,";
        var sut = CreateSut(db, new FakeProtector());

        await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, "CL", ProxyKind.DataCenter), CancellationToken.None);

        var proxy = await db.Proxies.SingleAsync();
        proxy.Geolocation.ShouldBe("CL");
        proxy.Kind.ShouldBe(ProxyKind.DataCenter);
    }

    [Fact]
    public async Task Handle_Should_Throw_When_RowOmitsCredentials_And_NoDefaultConfigured()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs - file", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,,,CL,DataCenter";
        var sut = CreateSut(db, new FakeProtector());

        await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_ReportRowErrors_Without_FailingTheWholeImport()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\n,7000,Http,u,p,,\n89.249.195.245,7000,Http,u,p,,";
        var sut = CreateSut(db, new FakeProtector());

        var result = await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().LineNumber.ShouldBe(2);
    }

    [Fact]
    public async Task Handle_Should_RetirePreviouslyImportedProxies_Missing_FromANewUpload()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeProtector());
        await sut.Handle(new SyncProviderAccountFromFileCommand(
            account.Id, $"{Header}\n89.249.195.245,7000,Http,u,p,,", null, null, null, null), CancellationToken.None);

        var result = await sut.Handle(new SyncProviderAccountFromFileCommand(
            account.Id, $"{Header}\n1.2.3.4,8000,Http,u,p,,", null, null, null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Retired.ShouldBe(1);
        (await db.Proxies.SingleAsync(p => p.Host == "89.249.195.245")).Status.ShouldBe(ProxyStatus.Retired);
    }

    [Fact]
    public async Task Handle_Should_Throw_And_NotRetireAnything_When_UploadHasZeroValidRecords()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        var existingProxy = Proxy.Create(
            account.Id, "89.249.195.245", 7000, ProxyProtocol.Http, "u", "p", "file:89.249.195.245:7000");
        existingProxy.SetStatus(ProxyStatus.Active);
        db.Proxies.Add(existingProxy);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeProtector());
        // Header row only — zero data rows, so the parser yields zero valid records.
        var csv = Header;

        await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None).AsTask());

        (await db.Proxies.SingleAsync(p => p.Id == existingProxy.Id)).Status.ShouldBe(ProxyStatus.Active);
        (await db.ProviderAccounts.SingleAsync()).LastSyncStatus.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_MergeDefaultCredentials_When_ALaterUploadOmitsOnlyOneOfThem()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs - file", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeProtector());
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,,,CL,DataCenter";
        await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, "u1", "p1", null, null), CancellationToken.None);

        await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, "u2", null, null, null), CancellationToken.None);

        var stored = await db.ProviderAccounts.SingleAsync();
        var storedDefaults = JsonSerializer.Deserialize<FileImportDefaultCredentials>(stored.ProtectedCredentials)!;
        storedDefaults.Username.ShouldBe("u2");
        storedDefaults.Password.ShouldBe("p1");
    }

    [Fact]
    public async Task Handle_Should_Throw_And_NotTouchCredentials_When_AccountAlreadyHoldsIncompatiblyShapedCredentials()
    {
        await using var db = CreateDb();
        const string brightDataCredentials = "{\"ApiToken\":\"x\",\"Zone\":\"y\",\"CustomerId\":\"z\",\"GatewayPort\":1}";
        var account = ProviderAccount.Create("BrightData - live", ProxyProviderType.BrightData, brightDataCredentials);
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeProtector());
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,u,p,CL,DataCenter";

        await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new SyncProviderAccountFromFileCommand(account.Id, csv, "newuser", "newpass", null, null), CancellationToken.None).AsTask());

        (await db.ProviderAccounts.SingleAsync()).ProtectedCredentials.ShouldBe(brightDataCredentials);
    }

    [Fact]
    public async Task Handle_Should_Throw_When_TargetingTheManualProviderAccount()
    {
        await using var db = CreateDb();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var initializer = new ProxiesDbInitializer(
            db, configuration, environment, new FakeProtector(), NullLogger<ProxiesDbInitializer>.Instance);
        await initializer.SeedAsync(CancellationToken.None);
        var sut = CreateSut(db, new FakeProtector());
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,u,p,CL,DataCenter";

        await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new SyncProviderAccountFromFileCommand(ManualProviderAccount.Id, csv, null, null, null, null), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_Throw_CustomException_Not_JsonException_When_StoredCredentialsAreMalformedJson()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs - file", ProxyProviderType.Oxylabs, "not valid json {{");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeProtector());
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,,,CL,DataCenter";

        var ex = await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None).AsTask());

        ex.Message.ShouldContain("no default credentials are configured");
    }
}
