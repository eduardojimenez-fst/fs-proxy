using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.SetProxiesStatus;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ProxyStatusHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_SetStatus_ForExplicitIds()
    {
        await using var db = CreateDb();
        var p1 = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        var p2 = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.Proxies.AddRange(p1, p2);
        await db.SaveChangesAsync();
        var sut = new SetProxiesStatusCommandHandler(db);

        var affected = await sut.Handle(new SetProxiesStatusCommand([p1.Id], null, ProxyStatus.Disabled), CancellationToken.None);

        affected.ShouldBe(1);
        (await db.Proxies.SingleAsync(x => x.Id == p1.Id)).Status.ShouldBe(ProxyStatus.Disabled);
        (await db.Proxies.SingleAsync(x => x.Id == p2.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }

    [Fact]
    public async Task Handle_Should_SetStatus_ForAllProxiesWithTag()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var p1 = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        p1.AssignTag(tag.Id);
        var p2 = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.Tags.Add(tag);
        db.Proxies.AddRange(p1, p2);
        await db.SaveChangesAsync();
        var sut = new SetProxiesStatusCommandHandler(db);

        var affected = await sut.Handle(new SetProxiesStatusCommand(null, tag.Id, ProxyStatus.Active), CancellationToken.None);

        affected.ShouldBe(1);
        (await db.Proxies.SingleAsync(x => x.Id == p1.Id)).Status.ShouldBe(ProxyStatus.Active);
        (await db.Proxies.SingleAsync(x => x.Id == p2.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }
}
