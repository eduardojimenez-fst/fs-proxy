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
