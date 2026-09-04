using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;
using FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;
using FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ProxyTagAssignmentHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SetProxyTags_Should_ReplaceFullTagSet()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var oldTag = Tag.Create("old-tag");
        var proxy = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(oldTag.Id);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(oldTag);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new SetProxyTagsCommandHandler(db);

        await sut.Handle(new SetProxyTagsCommand(proxy.Id, ["pais:cl", "funcionalidad:licitaciones"]), CancellationToken.None);

        var reloaded = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == proxy.Id);
        var tagNames = await db.Tags.Where(t => reloaded.TagAssignments.Select(a => a.TagId).Contains(t.Id)).Select(t => t.Name).ToListAsync();
        tagNames.ShouldBe(["funcionalidad:licitaciones", "pais:cl"], ignoreOrder: true);
    }

    [Fact]
    public async Task AssignProxyTag_Should_CreateTagAndAssignToEveryProxy_WithoutTouchingExistingTags()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var existingTag = Tag.Create("keep-me");
        var proxy1 = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        proxy1.AssignTag(existingTag.Id);
        var proxy2 = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(existingTag);
        db.Proxies.AddRange(proxy1, proxy2);
        await db.SaveChangesAsync();
        var sut = new AssignProxyTagCommandHandler(db);

        var touched = await sut.Handle(new AssignProxyTagCommand([proxy1.Id, proxy2.Id], "pais:cl"), CancellationToken.None);

        touched.ShouldBe(2);
        var newTag = await db.Tags.SingleAsync(t => t.Name == "pais:cl");
        var p1 = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == proxy1.Id);
        p1.TagAssignments.Select(a => a.TagId).ShouldContain(existingTag.Id);
        p1.TagAssignments.Select(a => a.TagId).ShouldContain(newTag.Id);
    }

    [Fact]
    public async Task UnassignProxyTag_Should_RemoveFromEveryProxy_And_ReturnZero_When_TagUnknown()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var tag = Tag.Create("pais:cl");
        var proxy = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(tag.Id);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(tag);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new UnassignProxyTagCommandHandler(db);

        var touched = await sut.Handle(new UnassignProxyTagCommand([proxy.Id], "pais:cl"), CancellationToken.None);

        touched.ShouldBe(1);
        (await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == proxy.Id)).TagAssignments.ShouldBeEmpty();

        var unknownTagTouched = await sut.Handle(new UnassignProxyTagCommand([proxy.Id], "no-such-tag"), CancellationToken.None);
        unknownTagTouched.ShouldBe(0);
    }
}
