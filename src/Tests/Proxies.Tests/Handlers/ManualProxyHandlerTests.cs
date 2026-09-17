using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;
using FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ManualProxyHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_AttachToManualAccount_And_CreateNewTags()
    {
        await using var db = CreateDb();
        var sut = new CreateManualProxyCommandHandler(db, new FakePasswordProtector());
        var command = new CreateManualProxyCommand("10.0.0.5", 3128, ProxyProtocol.Http, "u", "p", ["pais:cl", "funcionalidad:licitaciones"]);

        var id = await sut.Handle(command, CancellationToken.None);

        var stored = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == id);
        stored.ProviderAccountId.ShouldBe(ManualProviderAccount.Id);
        stored.TagAssignments.Count.ShouldBe(2);
        (await db.Tags.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Create_Should_ReuseExistingTag_When_NameAlreadyExists()
    {
        await using var db = CreateDb();
        db.Tags.Add(Tag.Create("pais:cl"));
        await db.SaveChangesAsync();
        var sut = new CreateManualProxyCommandHandler(db, new FakePasswordProtector());

        await sut.Handle(new CreateManualProxyCommand("10.0.0.6", 3128, ProxyProtocol.Http, null, null, ["PAIS:CL"]), CancellationToken.None);

        (await db.Tags.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Update_Should_KeepExistingPassword_When_PlaintextPasswordNotProvided()
    {
        await using var db = CreateDb();
        var protector = new FakePasswordProtector();
        var createSut = new CreateManualProxyCommandHandler(db, protector);
        var id = await createSut.Handle(
            new CreateManualProxyCommand("10.0.0.8", 3128, ProxyProtocol.Http, "u", "original-pass", ["pais:cl"]),
            CancellationToken.None);
        var updateSut = new UpdateManualProxyCommandHandler(db, protector);

        await updateSut.Handle(
            new UpdateManualProxyCommand(id, "10.0.0.9", 3129, ProxyProtocol.Http, "u", null, ["pais:cl", "funcionalidad:licitaciones"]),
            CancellationToken.None);

        var stored = await db.Proxies.SingleAsync(x => x.Id == id);
        stored.Host.ShouldBe("10.0.0.9");
        stored.ProtectedPassword.ShouldBe(protector.Protect("original-pass"));
    }

    [Fact]
    public async Task Update_Should_PreserveProviderMetadata_NotModelledByTheCommand()
    {
        // Regression: UpdateManualProxyCommand carries no geolocation/grouping/kind, so the
        // handler must pass the proxy's current values to UpdateConnection. Omitting them
        // blanked all three on every manual edit.
        await using var db = CreateDb();
        var protector = new FakePasswordProtector();
        var proxy = Proxy.Create(
            ManualProviderAccount.Id, "10.0.0.8", 3128, ProxyProtocol.Http, "u", protector.Protect("p"), null,
            geolocation: "cl", providerGrouping: "zone1", kind: ProxyKind.Residential);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new UpdateManualProxyCommandHandler(db, protector);

        await sut.Handle(
            new UpdateManualProxyCommand(proxy.Id, "10.0.0.9", 3129, ProxyProtocol.Http, "u", null, []),
            CancellationToken.None);

        var stored = await db.Proxies.SingleAsync(x => x.Id == proxy.Id);
        stored.Host.ShouldBe("10.0.0.9");
        stored.Geolocation.ShouldBe("cl");
        stored.ProviderGrouping.ShouldBe("zone1");
        stored.Kind.ShouldBe(ProxyKind.Residential);
    }

    [Fact]
    public async Task Update_Should_ReplaceUsername_When_Provided()
    {
        await using var db = CreateDb();
        var protector = new FakePasswordProtector();
        var proxy = Proxy.Create(
            ManualProviderAccount.Id, "10.0.0.8", 3128, ProxyProtocol.Http, "200.1.2.3", protector.Protect("p"), null,
            geolocation: null, providerGrouping: null, kind: null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new UpdateManualProxyCommandHandler(db, protector);

        await sut.Handle(
            new UpdateManualProxyCommand(proxy.Id, "10.0.0.8", 3128, ProxyProtocol.Http, "200.9.9.9", null, []),
            CancellationToken.None);

        (await db.Proxies.SingleAsync(x => x.Id == proxy.Id)).Username.ShouldBe("200.9.9.9");
    }

    [Fact]
    public async Task Delete_Should_RemoveProxy()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "10.0.0.7", 3128, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new DeleteManualProxyCommandHandler(db);

        await sut.Handle(new DeleteManualProxyCommand(proxy.Id), CancellationToken.None);

        (await db.Proxies.AnyAsync(x => x.Id == proxy.Id)).ShouldBeFalse();
    }

    private sealed class FakePasswordProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string ciphertext) => ciphertext;
    }
}
