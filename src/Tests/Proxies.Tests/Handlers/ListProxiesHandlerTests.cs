using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ListProxiesHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_FilterByTag()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var tag = Tag.Create("pais:cl");
        var matching = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        matching.AssignTag(tag.Id);
        var other = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(tag);
        db.Proxies.AddRange(matching, other);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(["pais:cl"], null, null), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([matching.Id]);
        result.Items.Single().Tags.ShouldBe(["pais:cl"]);
    }

    [Fact]
    public async Task Handle_Should_FilterByStatus()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var active = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        active.SetStatus(ProxyStatus.Active);
        var disabled = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        disabled.SetStatus(ProxyStatus.Disabled);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(active, disabled);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, ProxyStatus.Active, null), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([active.Id]);
    }
}
